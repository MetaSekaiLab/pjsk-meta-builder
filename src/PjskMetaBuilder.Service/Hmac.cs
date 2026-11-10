using System.Security.Cryptography;
using System.Text;

namespace PjskMetaBuilder.Service;

/// <summary>
/// 设计文档 §7.3：双向共享密钥签名。
///   X-Moe-Timestamp: unix 秒
///   X-Moe-Signature: sha256=hex(HMAC_SHA256(secret, timestamp + "." + body))
/// 时间戳与本机相差超过 5 分钟拒绝；签名用常量时间比较。
/// </summary>
public static class Hmac
{
    public const string TimestampHeader = "X-Moe-Timestamp";
    public const string SignatureHeader = "X-Moe-Signature";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static string Sign(byte[] secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, message));
    }

    /// <summary>校验通过返回 null，否则返回拒绝原因（只用于日志，不回给调用方细节）。</summary>
    public static string? Verify(byte[] secret, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader))
            return "缺少签名头";
        if (!long.TryParse(timestampHeader, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return "时间戳格式不对";
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > (long)Window.TotalSeconds)
            return "时间戳超出 5 分钟窗口";
        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts, body));
        var actual = Encoding.ASCII.GetBytes(signatureHeader);
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? null : "签名不匹配";
    }
}
