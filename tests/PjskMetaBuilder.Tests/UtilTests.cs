using System.Text;
using PjskMetaBuilder.Core.Output;
using PjskMetaBuilder.Core.Util;

namespace PjskMetaBuilder.Tests;

public class BitStringTests
{
    [Fact]
    public void EncodesHighBitFirstInByteOrder()
    {
        Assert.Equal("0001001011111111", BitString.Encode([0x12, 0xFF]));
    }

    [Fact]
    public void RoundTrips16Bytes()
    {
        var bytes = Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 3)).ToArray();
        var text = BitString.Encode(bytes);
        Assert.Equal(128, text.Length);
        Assert.Equal(bytes, BitString.Decode(text));
    }

    [Theory]
    [InlineData("0101")]
    [InlineData("2")]
    public void RejectsWrongLengthOrCharacters(string bad)
    {
        var text = bad.Length == 1 ? new string('0', 127) + bad : bad;
        Assert.Throws<FormatException>(() => BitString.Decode(text));
    }
}

public class AppVersionTests
{
    [Theory]
    [InlineData("6.10.0", "6.9.1", 1)]
    [InlineData("6.4.1", "6.4.1", 0)]
    [InlineData("6.4", "6.4.0", 0)]
    [InlineData("7.0.0", "7.0.1", -1)]
    public void ComparesNumerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(AppVersion.Compare(a, b)));
    }

    [Theory]
    [InlineData("7.1.0", true)]
    [InlineData("7.1.0.10", true)]
    [InlineData("7", false)]
    [InlineData("7.1.x", false)]
    public void Validates(string version, bool valid) => Assert.Equal(valid, AppVersion.IsValid(version));
}

public class EncodingTests
{
    [Fact]
    public void Base85MatchesPython() =>
        // python3 -c "import base64; print(base64.b85encode(b'pjsk-meta-builder\x00\x01\xff').decode())"
        Assert.Equal(Encoding.ASCII.GetBytes("pjsk-meta-builder").Concat(new byte[] { 0, 1, 0xFF }),
            Base85.Decode("aB6dFEp26VVJ%{HX>4R=asUDU"));

    [Fact]
    public void Crc32MatchesZlib() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
}
