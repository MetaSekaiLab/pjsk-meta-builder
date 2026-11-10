using System.Globalization;
using System.Text.Json;

namespace PjskMetaBuilder.Core.Keys;

/// <summary>
/// 从 Il2CppDumper 的 script.json 里只取两样东西（文件可达数百 MB，用流式读取，不构建对象树）：
///   1. <c>APIManager..cctor</c> 的地址（ScriptMethod 里 Name 以 <c>APIManager$$.cctor</c> 结尾的项）；
///   2. <c>Field$&lt;PrivateImplementationDetails&gt;.&lt;HASH&gt;</c> 的「使用槽 VA → HASH」映射（ScriptMetadata）。
/// cctor 里 InitializeArray 的源槽经重定位 addend 指到这些使用槽，于是得到字段 HASH。
/// </summary>
public sealed class Il2CppScript
{
    public ulong? ApiManagerCctor { get; private set; }
    public IReadOnlyDictionary<ulong, string> PrivateImplFields => _fields;

    private readonly Dictionary<ulong, string> _fields = new();

    private const string FieldPrefix = "Field$<PrivateImplementationDetails>.";
    private const string CctorSuffix = "APIManager$$.cctor";

    public static Il2CppScript Load(string path)
    {
        var script = new Il2CppScript();
        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        // 顶层对象里，逐个属性处理；只深入 ScriptMethod 和 ScriptMetadata，其余数组整段跳过
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "ScriptMethod": script.ReadMethods(ref reader); break;
                case "ScriptMetadata": script.ReadMetadata(ref reader); break;
                default: reader.Skip(); break;
            }
        }
        return script;
    }

    private void ReadMethods(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            ulong? address = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.GetString();
                reader.Read();
                if (field == "Name") name = reader.GetString();
                else if (field == "Address") address = ReadAddress(ref reader);
                else reader.Skip();
            }
            if (ApiManagerCctor is null && name is not null && name.EndsWith(CctorSuffix, StringComparison.Ordinal))
                ApiManagerCctor = address;
        }
    }

    private void ReadMetadata(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            ulong address = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var field = reader.GetString();
                reader.Read();
                if (field == "Name") name = reader.GetString();
                else if (field == "Address") address = ReadAddress(ref reader) ?? 0;
                else reader.Skip();
            }
            if (name is not null && name.StartsWith(FieldPrefix, StringComparison.Ordinal))
                _fields[address] = name[FieldPrefix.Length..];
        }
    }

    /// <summary>Address 字段可能是 JSON 数字，也可能是 "0x…" / 十进制字符串。reader 停在该值上。</summary>
    private static ulong? ReadAddress(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetUInt64();
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
            return null;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(s, CultureInfo.InvariantCulture);
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type)
            throw new DataException($"script.json 格式异常：期望 {type}，实际 {reader.TokenType}");
    }
}
