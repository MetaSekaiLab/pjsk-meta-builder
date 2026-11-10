using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PjskMetaBuilder.Core.Publish;

/// <summary>
/// 发布前的内容检查（设计文档 §5.7）。只允许三个文件；moe-info.json 是唯一允许出现密钥的文件。
/// 任何一项不通过都抛 DataException，不发布。
/// </summary>
public static partial class ContentLinter
{
    public static readonly string[] AllowedFiles = ["meta.json", "moe-info.json", "schema.json"];

    private static readonly HashSet<string> MetaKeys = ["schema", "region", "appVersion", "appHash", "dataVersion",
        "unityVersion", "clientVersion", "abVersion", "package", "files", "generatedAt", "builder"];
    private static readonly HashSet<string> PackageKeys = ["name", "versionName", "versionCode", "sha256", "source"];
    private static readonly HashSet<string> BuilderKeys = ["version", "il2cppDumper"];
    private static readonly HashSet<string> SchemaTopKeys = ["schema", "source", "tables", "classes"];

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex Uuid();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex("[0-9A-Fa-f]{32,}")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"\b(KEY|IV|XOR|SECRET|TOKEN)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenWord();

    [GeneratedRegex("^[01]{128}$")]
    private static partial Regex BitString128();

    [GeneratedRegex(@"^[A-Za-z0-9_.`/<>+\-]*$")]
    private static partial Regex Identifier();

    /// <summary>检查一个版本目录的全部文件；返回 meta.json 解析结果。</summary>
    public static JsonElement CheckDirectory(IReadOnlyDictionary<string, byte[]> files, bool nuverse)
    {
        var problems = new List<string>();
        foreach (var name in files.Keys.Where(n => !AllowedFiles.Contains(n)))
            problems.Add($"不允许的文件：{name}");
        if (!files.ContainsKey("meta.json") || !files.ContainsKey("moe-info.json"))
            throw new DataException("缺少 meta.json 或 moe-info.json");
        if (nuverse != files.ContainsKey("schema.json"))
            problems.Add(nuverse ? "Nuverse 服缺少 schema.json" : "CP 服不应有 schema.json");

        var meta = JsonDocument.Parse(files["meta.json"]).RootElement.Clone();
        CheckMeta(meta, problems);
        CheckMoeInfo(files["moe-info.json"], nuverse, problems);
        if (files.TryGetValue("schema.json", out var schema))
            CheckSchema(schema, problems);

        // meta.json 的 files 必须与实际文件逐一对上
        if (meta.TryGetProperty("files", out var listed) && listed.ValueKind == JsonValueKind.Object)
        {
            var expected = files.Keys.Where(n => n != "meta.json").OrderBy(n => n, StringComparer.Ordinal).ToList();
            var actual = listed.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (!expected.SequenceEqual(actual))
                problems.Add($"meta.json files 列出的是 [{string.Join(", ", actual)}]，实际文件是 [{string.Join(", ", expected)}]");
            foreach (var p in listed.EnumerateObject())
                if (files.TryGetValue(p.Name, out var bytes)
                    && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != p.Value.GetString())
                    problems.Add($"{p.Name} 的 sha256 与 meta.json 记录不符");
        }

        if (problems.Count > 0)
            throw new DataException("内容检查未通过：" + string.Join("；", problems));
        return meta;
    }

    private static void CheckMeta(JsonElement meta, List<string> problems)
    {
        CheckKeys(meta, MetaKeys, "meta.json", problems, exact: true);
        if (meta.TryGetProperty("appHash", out var hash) && !Uuid().IsMatch(hash.GetString() ?? ""))
            problems.Add("appHash 不是 UUID");
        if (meta.TryGetProperty("package", out var pkg))
        {
            CheckKeys(pkg, PackageKeys, "meta.json package", problems, exact: true);
            if (pkg.TryGetProperty("sha256", out var sha) && !Sha256().IsMatch(sha.GetString() ?? ""))
                problems.Add("package.sha256 格式不对");
            if (pkg.TryGetProperty("versionCode", out var vc) && vc.ValueKind != JsonValueKind.Number)
                problems.Add("package.versionCode 不是数字");
        }
        if (meta.TryGetProperty("builder", out var builder))
            CheckKeys(builder, BuilderKeys, "meta.json builder", problems, exact: true);
        if (meta.TryGetProperty("files", out var files))
            foreach (var f in files.EnumerateObject())
                if (!Sha256().IsMatch(f.Value.GetString() ?? ""))
                    problems.Add($"files.{f.Name} 不是 sha256");
        ScanStrings(meta, "meta.json", problems, allowHexIn: ["sha256", "files"]);
    }

    private static void CheckMoeInfo(byte[] bytes, bool nuverse, List<string> problems)
    {
        var root = JsonDocument.Parse(bytes).RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            problems.Add("moe-info.json 必须是数组");
            return;
        }
        var expected = nuverse ? 2 : 1;
        if (root.GetArrayLength() != expected)
            problems.Add($"moe-info.json 应有 {expected} 项，实际 {root.GetArrayLength()} 项");
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problems.Add("moe-info.json 的每一项必须是对象");
                continue;
            }
            CheckKeys(entry, ["Moe-Key", "Moe-Secret"], "moe-info.json", problems, exact: true);
            foreach (var p in entry.EnumerateObject())
                if (!BitString128().IsMatch(p.Value.GetString() ?? ""))
                    problems.Add($"moe-info.json 的 {p.Name} 不是 128 位 0/1 串");
        }
    }

    private static void CheckSchema(byte[] bytes, List<string> problems)
    {
        var root = JsonDocument.Parse(bytes).RootElement;
        CheckKeys(root, SchemaTopKeys, "schema.json", problems, exact: true);
        var before = problems.Count;
        Walk(root, "schema.json", (path, value) =>
        {
            if (LongHex().IsMatch(value))
                problems.Add($"{path} 含有 32 位以上的十六进制串");
            else if (!Identifier().IsMatch(value) && !path.EndsWith(".appVersion", StringComparison.Ordinal))
                problems.Add($"{path} 含有非标识符字符");
        }, keyCheck: key =>
        {
            if (!Identifier().IsMatch(key))
                problems.Add($"schema.json 的键 {key} 含有非标识符字符");
        });
        if (problems.Count - before > 20)
            problems.RemoveRange(before + 20, problems.Count - before - 20);
    }

    private static void ScanStrings(JsonElement root, string file, List<string> problems, string[] allowHexIn) =>
        Walk(root, file, (path, value) =>
        {
            var allowHex = allowHexIn.Any(a => path.Contains($".{a}", StringComparison.Ordinal));
            if (!allowHex && LongHex().IsMatch(value))
                problems.Add($"{path} 含有 32 位以上的十六进制串");
            if (ForbiddenWord().IsMatch(value))
                problems.Add($"{path} 含有禁止出现的词");
        });

    private static void Walk(JsonElement e, string path, Action<string, string> onString, Action<string>? keyCheck = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    keyCheck?.Invoke(p.Name);
                    Walk(p.Value, $"{path}.{p.Name}", onString, keyCheck);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                    Walk(item, $"{path}[{i++}]", onString, keyCheck);
                break;
            case JsonValueKind.String:
                onString(path, e.GetString()!);
                break;
        }
    }

    private static void CheckKeys(JsonElement obj, IEnumerable<string> allowed, string where, List<string> problems, bool exact)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{where} 必须是对象");
            return;
        }
        var set = allowed.ToHashSet();
        var keys = obj.EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var k in keys.Except(set))
            problems.Add($"{where} 出现了白名单以外的键 {k}");
        if (exact)
            foreach (var k in set.Except(keys))
                problems.Add($"{where} 缺少键 {k}");
    }
}
