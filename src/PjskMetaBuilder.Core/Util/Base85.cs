namespace PjskMetaBuilder.Core.Util;

/// <summary>Base85 解码，字母表与 Python <c>base64.b85decode</c>（RFC 1924）相同。</summary>
public static class Base85
{
    private const string Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";

    private static readonly int[] Lookup = BuildLookup();

    private static int[] BuildLookup()
    {
        var table = Enumerable.Repeat(-1, 128).ToArray();
        for (var i = 0; i < Alphabet.Length; i++)
            table[Alphabet[i]] = i;
        return table;
    }

    public static byte[] Decode(string text)
    {
        var padding = (5 - text.Length % 5) % 5;
        var padded = text + new string('~', padding);
        var output = new List<byte>(padded.Length / 5 * 4);
        for (var i = 0; i < padded.Length; i += 5)
        {
            ulong acc = 0;
            for (var j = 0; j < 5; j++)
            {
                var c = padded[i + j];
                var v = c < 128 ? Lookup[c] : -1;
                if (v < 0)
                    throw new FormatException($"Base85 非法字符：{c}");
                acc = acc * 85 + (ulong)v;
            }
            if (acc > uint.MaxValue)
                throw new FormatException("Base85 数据溢出");
            output.Add((byte)(acc >> 24));
            output.Add((byte)(acc >> 16));
            output.Add((byte)(acc >> 8));
            output.Add((byte)acc);
        }
        output.RemoveRange(output.Count - padding, padding);
        return output.ToArray();
    }
}
