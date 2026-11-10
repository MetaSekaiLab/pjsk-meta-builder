namespace PjskMetaBuilder.Core.Output;

/// <summary>moe-info.json 的 0/1 位串：每字节 8 位、高位在前，按字节顺序拼接（16 字节 → 128 字符）。</summary>
public static class BitString
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 8];
        for (var i = 0; i < bytes.Length; i++)
            for (var bit = 0; bit < 8; bit++)
                chars[i * 8 + bit] = ((bytes[i] >> (7 - bit)) & 1) != 0 ? '1' : '0';
        return new string(chars);
    }

    public static byte[] Decode(string text, int expectedBytes = 16)
    {
        if (text.Length != expectedBytes * 8)
            throw new FormatException($"位串长度应为 {expectedBytes * 8}，实际 {text.Length}");
        var bytes = new byte[expectedBytes];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '0' && c != '1')
                throw new FormatException($"位串第 {i} 个字符不是 0/1");
            if (c == '1')
                bytes[i / 8] |= (byte)(1 << (7 - i % 8));
        }
        return bytes;
    }
}
