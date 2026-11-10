using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PjskMetaBuilder.Core.Keys;

public sealed record AesKey(byte[] Key, byte[] Iv);

/// <summary>[0] = API 密钥（APIManager.dummydata/stab）；[1] = ab 密钥（abdummydata/abstab，只有 Nuverse 服有）。</summary>
public sealed record KeySet(AesKey Api, AesKey? Ab);

/// <summary>
/// 第一版的密钥定位：客户端 &lt;PrivateImplementationDetails&gt; 的字段名就是数据内容的 SHA-256，
/// 数据本身在解密后 metadata 的默认值数据区里。按已知哈希在这一区域查找 16 字节常量。
/// 已知哈希来自 jp 7.1.0 / hmt 6.4.0 / cn 6.4.1 的反编译（设计文档 §5.5）；命中不了就报错，人工处理。
/// 以后用反汇编 APIManager..cctor 的方式替换（设计文档 §9 Q1）。
/// </summary>
public static class KeyExtractor
{
    // APIManager.dummydata / stab（CP 服）= abdummydata / abstab（Nuverse 服）
    private const string SegaKey = "5B30CBC189496757FF32D0274D9465B685FF2626094DFEDFCDBD2613228FD862";
    private const string SegaIv = "7C1CF10C46BDB8AD8B2971E843E721F35EEFE1478033539CAF3210F90093F1BA";
    // APIManager.dummydata / stab（Nuverse 服）
    private const string NuverseKey = "BA2B1D52DD967A490AC6141B7493EB6694AC0B8A01F1657DB17C1D1490EE1F72";
    private const string NuverseIv = "3673BB77DDD2423EEA4522AA23A9BFB8956F06D0288633DD51BA14364C0C5855";

    public static KeySet Extract(byte[] metadata, bool nuverse)
    {
        var wanted = nuverse
            ? new[] { NuverseKey, NuverseIv, SegaKey, SegaIv }
            : new[] { SegaKey, SegaIv };
        var found = Scan(metadata, wanted, DefaultValueDataRange(metadata));
        if (found.Count < wanted.Length)
            found = Scan(metadata, wanted, (0, metadata.Length)); // 区间判断有误时退回整文件扫描

        byte[] Need(string hash) => found.TryGetValue(hash, out var v) ? v
            : throw new DataException($"metadata 里找不到已知密钥（SHA-256 {hash[..8]}…）；密钥可能已更换，需要人工处理");

        return nuverse
            ? new KeySet(new AesKey(Need(NuverseKey), Need(NuverseIv)), new AesKey(Need(SegaKey), Need(SegaIv)))
            : new KeySet(new AesKey(Need(SegaKey), Need(SegaIv)), null);
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
