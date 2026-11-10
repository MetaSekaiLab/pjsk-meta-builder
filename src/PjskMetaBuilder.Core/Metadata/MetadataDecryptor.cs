using System.Buffers.Binary;
using PjskMetaBuilder.Core.Util;

namespace PjskMetaBuilder.Core.Metadata;

/// <summary>
/// 解密 global-metadata.dat。移植自 pjsk-reverse-workflow scripts/sekai_metadata.py 的 decrypt_metadata：
/// 先识别格式，绝不把 jp/en 的方式套到 cn/tw/kr 上。
/// </summary>
public static class MetadataDecryptor
{
    public const int SupportedVersion = 31;

    public static byte[] Decrypt(byte[] encrypted, ReadOnlySpan<byte> libil2cpp)
    {
        if (encrypted.Length < 8)
            throw new DataException("metadata 文件太小");

        if (encrypted.AsSpan(0, 8).SequenceEqual(MetadataConstants.NuverseSentinel))
            return DecryptNuverse(encrypted);

        if (encrypted.AsSpan(0, 4).SequenceEqual(MetadataConstants.Magic))
            throw new DataException("metadata 已经是明文，不需要解密");

        return DecryptJp(encrypted, libil2cpp);
    }

    /// <summary>cn/tw/kr：哨兵之后是带魔数和版本号的头，头里给出需要 XOR 的区间。</summary>
    private static byte[] DecryptNuverse(byte[] encrypted)
    {
        var metadata = encrypted.AsSpan(8).ToArray();
        if (metadata.Length < 0x20
            || !metadata.AsSpan(0, 4).SequenceEqual(MetadataConstants.Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(4)) != SupportedVersion)
            throw new DataException("不支持的 Nuverse metadata 头或版本");

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(0x18));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(0x1C));
        if (offset < 0x20 || (long)offset + size > metadata.Length)
            throw new DataException("metadata XOR 区间越界");

        var preimages = Base85.Decode(MetadataConstants.NuverseKeyTableBase85);
        var words = new byte[preimages.Length / 4][];
        for (var i = 0; i < words.Length; i++)
        {
            var crc = Crc32.Compute(preimages.AsSpan(i * 4, 4));
            words[i] = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(words[i], crc);
        }

        for (long i = 0; i < size; i += 4)
        {
            var word = words[((i & 0xFF) + ((i >> 7) & 0xFF)) & 0x7F];
            for (var j = 0; j < 4 && i + j < size; j++)
                metadata[offset + i + j] ^= word[j];
        }
        return metadata;
    }

    /// <summary>jp/en：128 字节 XOR 密钥紧跟在 libil2cpp.so 中唯一的 NDK 字符串之后。</summary>
    private static byte[] DecryptJp(byte[] encrypted, ReadOnlySpan<byte> so)
    {
        var sig = MetadataConstants.JpKeySignature;
        var first = so.IndexOf(sig);
        if (first < 0 || so[(first + 1)..].IndexOf(sig) >= 0)
            throw new DataException("不支持的 metadata 格式：libil2cpp.so 里应当正好有一处密钥特征串");
        var start = first + sig.Length;
        if (start + 128 > so.Length)
            throw new DataException("metadata XOR 密钥被截断");
        var key = so.Slice(start, 128);

        var decrypted = new byte[encrypted.Length];
        for (var i = 0; i < encrypted.Length; i++)
            decrypted[i] = (byte)(encrypted[i] ^ key[i & 127]);
        if (!decrypted.AsSpan(0, 4).SequenceEqual(MetadataConstants.Magic))
            throw new DataException("解密后魔数不对");
        return decrypted;
    }
}
