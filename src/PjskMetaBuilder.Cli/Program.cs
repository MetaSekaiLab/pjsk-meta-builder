using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Keys;
using PjskMetaBuilder.Core.Metadata;
using PjskMetaBuilder.Core.Packages;
using PjskMetaBuilder.Core.Publish;
using PjskMetaBuilder.Core.Unity;

// 退出码：0 成功；75 网络类或来源未就绪（可重试）；65 数据类失败；64 用法错误；70 内部错误。
const int ExUsage = 64, ExData = 65, ExSoftware = 70, ExTempFail = 75;

try
{
    return args.FirstOrDefault() switch
    {
        "build" => Build(Options.Parse(args[1..])),
        "inspect" => Inspect(Options.Parse(args[1..])),
        "publish" => Publish(Options.Parse(args[1..])),
        "run" => RunAll(Options.Parse(args[1..])),
        _ => Usage(),
    };
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"参数错误：{e.Message}");
    return ExUsage;
}
catch (SourceNotReadyException e)
{
    Console.Error.WriteLine($"来源未就绪：{e.Message}");
    return ExTempFail;
}
catch (DataException e)
{
    Console.Error.WriteLine($"数据错误：{e.Message}");
    return ExData;
}
catch (HttpRequestException e)
{
    Console.Error.WriteLine($"网络错误：{e.Message}");
    return ExTempFail;
}
catch (Exception e)
{
    Console.Error.WriteLine($"内部错误：{e}");
    return ExSoftware;
}

static int Usage()
{
    Console.Error.WriteLine("""
        用法：
          pjsk-meta-builder build   --region <jp|cn|…> --apk <主包> [--apk <拆分包> …] --out <目录>
                                    [--source local] [--expected-version-code N]
                                    [--il2cppdumper <Il2CppDumper.dll>] [--dotnet <dotnet 路径>] [--keep-work]
          pjsk-meta-builder inspect --apk <主包> [--apk <拆分包> …]
          pjsk-meta-builder publish --region <地区> --dir <build 输出目录> [--repo owner/name|路径] [--branch main] [--dry-run]
          pjsk-meta-builder run     （build 的全部参数）+ [--repo …] [--branch …] [--dry-run]：build 后直接 publish

        publish 通过 GitHub API 读写（不克隆）；写入凭据（--dry-run 不需要）：
          GITHUB_APP_ID + GITHUB_APP_PRIVATE_KEY（PEM 内容）或 GITHUB_APP_PRIVATE_KEY_FILE
        """);
    return ExUsage;
}

static int Build(Options o)
{
    BuildCore(o);
    return 0;
}

static int RunAll(Options o)
{
    BuildCore(o);
    return PublishDir(o, o.Required("out"));
}

static int Publish(Options o) => PublishDir(o, o.Required("dir"));

static int PublishDir(Options o, string dir)
{
    var repo = o.Get("repo") ?? Environment.GetEnvironmentVariable("APP_META_REPO") ?? "StarMoe-org/moesekai-app-meta";
    var dryRun = o.Has("dry-run");
    string? token = null;
    // dry-run 有凭据就用（避免匿名 API 的限流），没有就匿名读公开仓库
    var haveCredentials = Environment.GetEnvironmentVariable("GITHUB_APP_ID") is { Length: > 0 };
    if (Git.IsGitHub(repo) && (!dryRun || haveCredentials))
    {
        var appId = Environment.GetEnvironmentVariable("GITHUB_APP_ID")
            ?? throw new ArgumentException("缺少 GITHUB_APP_ID");
        var pem = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY")
            ?? (Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY_FILE") is { } f ? File.ReadAllText(f) : null)
            ?? throw new ArgumentException("缺少 GITHUB_APP_PRIVATE_KEY / GITHUB_APP_PRIVATE_KEY_FILE");
        var parts = repo.Split('/');
        token = new GitHubAppToken(appId, pem).CreateInstallationTokenAsync(parts[0], parts[1]).GetAwaiter().GetResult();
    }
    var result = Publisher.Publish(new PublishRequest(o.Required("region"), dir, repo, o.Get("branch") ?? "main", token, dryRun));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
    return result.Result == "rejected" ? ExData : 0;
}

static void BuildCore(Options o)
{
    var result = MetaBuilder.Build(new BuildRequest(
        o.Required("region"), o.Apks(), o.Required("out"),
        o.Get("source") ?? "local",
        o.Get("expected-version-code") is { } e ? long.Parse(e) : null,
        o.Get("il2cppdumper") ?? Environment.GetEnvironmentVariable("IL2CPPDUMPER_DLL"),
        o.Get("dotnet"),
        o.Has("keep-work")));
    var m = result.Meta;
    Console.WriteLine($"{m.Region}: appVersion {m.AppVersion}, appHash {m.AppHash}, dataVersion {m.DataVersion}, versionCode {m.Package.VersionCode}");
    Console.WriteLine($"  密钥 [0] {KeyExtractor.Fingerprint(result.Keys.Api.Key)}/{KeyExtractor.Fingerprint(result.Keys.Api.Iv)}"
        + (result.Keys.Ab is { } ab ? $"，[1] {KeyExtractor.Fingerprint(ab.Key)}/{KeyExtractor.Fingerprint(ab.Iv)}" : ""));
    foreach (var (name, sha) in result.WrittenFiles)
        Console.WriteLine($"  {name}  sha256 {sha}");
    if (result.Schema is { } schema)
        Console.WriteLine($"  结构文件：{schema.Tables} 张表，{schema.Classes} 个类，{schema.Warnings.Count} 条警告");
    if (result.DummyDllDir is not null)
        Console.WriteLine($"  DummyDll：{result.DummyDllDir}");
    foreach (var w in result.Warnings)
        Console.Error.WriteLine($"警告：{w}");
}

static int Inspect(Options o)
{
    using var files = PackageFiles.Open(o.Apks());
    foreach (var (apk, bytes) in files.Manifests())
    {
        var info = AndroidManifest.Parse(bytes);
        Console.WriteLine($"{Path.GetFileName(apk)}: package={info.Package} versionName={info.VersionName} versionCode={info.VersionCode} split={info.Split ?? "-"}");
    }
    var s = PlayerSettingsReader.Read(files);
    Console.WriteLine($"production_android（{s.FoundIn}）：client {s.ClientVersion}，data {s.DataVersion}，ab {s.AbVersion}，appHash {s.ClientAppHash}，bundle {s.BundleIdentifier}，Unity {s.UnityVersion}");
    var region = files.Manifests().Select(m => AndroidManifest.Parse(m.Manifest)).Where(i => i.Split is null)
        .Select(i => Regions.ByPackage(i.Package)).FirstOrDefault();
    var metadata = MetadataDecryptor.Decrypt(files.Read(MetaBuilder.GlobalMetadata), files.Read(MetaBuilder.LibIl2Cpp));
    Console.WriteLine($"metadata：已解密，{metadata.Length} 字节");
    if (region is not null)
    {
        var keys = KeyExtractor.Extract(metadata, region.Nuverse);
        Console.WriteLine($"密钥（SHA-256 前缀）：[0] {KeyExtractor.Fingerprint(keys.Api.Key)}/{KeyExtractor.Fingerprint(keys.Api.Iv)}"
            + (keys.Ab is { } ab ? $"，[1] {KeyExtractor.Fingerprint(ab.Key)}/{KeyExtractor.Fingerprint(ab.Iv)}" : ""));
    }
    return 0;
}

sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                throw new ArgumentException($"无法识别的参数 {args[i]}");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            if (!o._values.TryGetValue(key, out var list))
                o._values[key] = list = [];
            list.Add(value);
        }
        return o;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v[^1] : null;
    public string Required(string key) => Get(key) is { Length: > 0 } v ? v : throw new ArgumentException($"缺少 --{key}");
    public List<string> Apks() => _values.TryGetValue("apk", out var v) && v.Count > 0 ? v : throw new ArgumentException("缺少 --apk");
}
