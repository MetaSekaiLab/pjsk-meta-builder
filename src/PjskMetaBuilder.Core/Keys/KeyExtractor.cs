using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PjskMetaBuilder.Core.Keys;

public sealed record AesKey(byte[] Key, byte[] Iv);

/// <summary>[0] = API 密钥（APIManager.dummydata/stab）；[1] = ab 密钥（abdummydata/abstab，只有 Nuverse 服有）。</summary>
public sealed record KeySet(AesKey Api, AesKey? Ab);

/// <summary>
/// 第一版的密钥定位：客户端 &lt;PrivateImplementationDetails&gt; 的字段名就是数据内容的 SHA-256，
/// 数据本身在解密后 metadata 的默认值数据区里。按已知哈希在这一区域查找 16 字节常量。
/// 已知哈希来自 jp 7.1.0 / hmt 6.4.0 / cn 6.4.1 的反编译，en 的来自 sssekai 的 en 密钥（en 6.1.0 实测命中）；
/// 命中不了就报错，人工处理（设计文档 §5.5）。
/// 以后用反汇编 APIManager..cctor 的方式替换（设计文档 §9 Q1）。
/// </summary>
public static class KeyExtractor
{
    // APIManager.dummydata / stab（jp）= abdummydata / abstab（Nuverse 服）
    private const string SegaKey = "5B30CBC189496757FF32D0274D9465B685FF2626094DFEDFCDBD2613228FD862";
    private const string SegaIv = "7C1CF10C46BDB8AD8B2971E843E721F35EEFE1478033539CAF3210F90093F1BA";
    // APIManager.dummydata / stab（Nuverse 服）
    private const string NuverseKey = "BA2B1D52DD967A490AC6141B7493EB6694AC0B8A01F1657DB17C1D1490EE1F72";
    private const string NuverseIv = "3673BB77DDD2423EEA4522AA23A9BFB8956F06D0288633DD51BA14364C0C5855";

    // APIManager.dummydata / stab（en，与 jp 不同）
    private const string EnKey = "D01AC7F5E061637A87C703FD84B716487DC7AAE7DD2447BB56091AB532BF5EA2";
    private const string EnIv = "2DBB0FDF897ABE78C49FECB839FEB25C7EA1F361C39E92388938C4520D16D176";

    /// <summary>已知哈希兜底法（方案 3）：按地区已知的字段哈希在 metadata 中取数据。</summary>
    public static KeySet Extract(byte[] metadata, RegionInfo region) =>
        FromHashes(metadata, KnownHashes(region), region.Nuverse);

    /// <summary>地区已知的有序密钥字段哈希（§5.5 的表），用于兜底和与 cctor 解析结果交叉校验。</summary>
    public static IReadOnlyList<string> KnownHashes(RegionInfo region)
    {
        var (apiKey, apiIv) = region.Nuverse ? (NuverseKey, NuverseIv) : region.Code == "en" ? (EnKey, EnIv) : (SegaKey, SegaIv);
        return region.Nuverse ? new[] { apiKey, apiIv, SegaKey, SegaIv } : new[] { apiKey, apiIv };
    }

    /// <summary>
    /// 按一组有序哈希组装 KeySet：[0][1] = API key/iv，[2][3] = ab key/iv（Nuverse）。
    /// 哈希可以来自已知表，也可以来自 cctor 反汇编（<see cref="KeyResolver"/>），所以不假设具体数值。
    /// </summary>
    public static KeySet FromHashes(byte[] metadata, IReadOnlyList<string> hashes, bool nuverse)
    {
        var expected = nuverse ? 4 : 2;
        if (hashes.Count != expected)
            throw new DataException($"{(nuverse ? "Nuverse" : "CP")} 服应有 {expected} 个密钥字段，实际 {hashes.Count}");
        var values = Find(metadata, hashes);
        return new KeySet(new AesKey(values[0], values[1]), nuverse ? new AesKey(values[2], values[3]) : null);
    }

    /// <summary>在 metadata 默认值数据区（兜底：整文件）按 SHA-256 定位每个 16 字节常量，按输入顺序返回。</summary>
    public static byte[][] Find(byte[] metadata, IReadOnlyList<string> hashes)
    {
        var wanted = hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = Scan(metadata, wanted, DefaultValueDataRange(metadata));
        if (found.Count < wanted.Length)
            found = Scan(metadata, wanted, (0, metadata.Length)); // 区间判断有误时退回整文件扫描
        return hashes.Select(h => found.TryGetValue(h, out var v) ? v
            : throw new DataException($"metadata 里找不到密钥字段（SHA-256 {h[..Math.Min(8, h.Length)]}…）；可能已更换，需要人工处理")).ToArray();
    }

    /// <summary>只给出 SHA-256 前缀，用于日志和 inspect，不暴露密钥本身。</summary>
    public static string Fingerprint(byte[] value) => Convert.ToHexString(SHA256.HashData(value))[..8];

    /// <summary>metadata v31 头：fieldAndParameterDefaultValueData 的 offset/size 位于 0x48/0x4C。</summary>
    private static (int Start, int Length) DefaultValueDataRange(byte[] metadata)
    {
        if (metadata.Length < 0x50)
            return (0, metadata.Length);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(0x48));
        var size = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(0x4C));
        if (offset <= 0 || size <= 0 || (long)offset + size > metadata.Length)
            return (0, metadata.Length);
        return (offset, size);
    }

    private static Dictionary<string, byte[]> Scan(byte[] data, string[] wanted, (int Start, int Length) range)
    {
        var targets = wanted.Select(Convert.FromHexString).ToArray();
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Span<byte> hash = stackalloc byte[32];
        var end = range.Start + range.Length - 16;
        for (var i = range.Start; i <= end && result.Count < wanted.Length; i++)
        {
            SHA256.HashData(data.AsSpan(i, 16), hash);
            for (var t = 0; t < targets.Length; t++)
            {
                if (hash.SequenceEqual(targets[t]))
                    result.TryAdd(wanted[t], data.AsSpan(i, 16).ToArray());
            }
        }
        return result;
    }
}
