using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PjskMetaBuilder.Core.Keys;

namespace PjskMetaBuilder.Core.Output;

public sealed record PackageMeta(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("versionName")] string VersionName,
    [property: JsonPropertyName("versionCode")] long VersionCode,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("source")] string Source);

public sealed record BuilderMeta(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("il2cppDumper")] string Il2CppDumper);

/// <summary>meta.json（设计文档 §4.2）。字段顺序即输出顺序。</summary>
public sealed record AppMeta(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("appHash")] string AppHash,
    [property: JsonPropertyName("dataVersion")] string DataVersion,
    [property: JsonPropertyName("unityVersion")] string UnityVersion,
    [property: JsonPropertyName("clientVersion")] string ClientVersion,
    [property: JsonPropertyName("abVersion")] string AbVersion,
    [property: JsonPropertyName("package")] PackageMeta Package,
    [property: JsonPropertyName("files")] SortedDictionary<string, string> Files,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("builder")] BuilderMeta Builder);

public sealed record MoeInfoEntry(
    [property: JsonPropertyName("Moe-Key")] string MoeKey,
    [property: JsonPropertyName("Moe-Secret")] string MoeSecret);

public static class Artifacts
{
    public const int MetaSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>两空格缩进、UTF-8 无 BOM、末尾一个换行。</summary>
    public static byte[] Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static List<MoeInfoEntry> MoeInfo(KeySet keys)
    {
        var list = new List<MoeInfoEntry> { Entry(keys.Api) };
        if (keys.Ab is not null)
            list.Add(Entry(keys.Ab));
        return list;
    }

    private static MoeInfoEntry Entry(AesKey key) => new(BitString.Encode(key.Key), BitString.Encode(key.Iv));

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
