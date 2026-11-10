using System.Net;
using System.Text.Json;

namespace PjskMetaBuilder.Core.Sources;

public sealed record QooAppCredentials(string Token, string? UserId = null, string? DeviceId = null)
{
    /// <summary>
    /// QOOAPP_TOKEN 或 QOOAPP_TOKEN_FILE（文件内容就是 token）；本机调试时也读 ~/.config/qooapp.env 里的 QOOAPP_TOKEN=。
    /// 可选 QOOAPP_USER_ID、QOOAPP_DEVICE_ID。
    /// </summary>
    public static QooAppCredentials FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("QOOAPP_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && Environment.GetEnvironmentVariable("QOOAPP_TOKEN_FILE") is { Length: > 0 } file)
            token = File.ReadAllText(file).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            var env = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "qooapp.env");
            if (File.Exists(env))
                token = File.ReadLines(env).Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("QOOAPP_TOKEN=", StringComparison.Ordinal))?["QOOAPP_TOKEN=".Length..].Trim();
        }
        if (string.IsNullOrWhiteSpace(token))
            throw new SourceConfigurationException("缺少 QooApp token：设置 QOOAPP_TOKEN 或 QOOAPP_TOKEN_FILE");
        return new(token, Environment.GetEnvironmentVariable("QOOAPP_USER_ID"), Environment.GetEnvironmentVariable("QOOAPP_DEVICE_ID"));
    }
}

/// <summary>
/// QooApp（设计文档 §5.1）：
/// 1. GET /store/v11/apps/{条目id} → packageId、apk.versionCode/versionName/baseApkMd5、splitApks[]{url, md5, size}
/// 2. 主包 GET /v11/apps/{包名}/download?type=app → 302 到文件地址；拆分包直接用 splitApks[].url
/// 版本号在下载前就能拿到，所以“来源还没更新”在下载前判断，不浪费流量。
/// </summary>
public sealed class QooAppSource(QooAppCredentials credentials, HttpClient? http = null, Action<string>? log = null,
    string apiBase = "https://api.qoo-app.com") : IPackageSource
{
    private const string ClientVersion = "9.4.3", ClientCode = "90403";
    private readonly Downloader _downloader = new(http ?? Downloader.CreateClient(), log);

    public string Name => "qooapp";

    public DownloadedPackage Fetch(RegionInfo region, string cacheDir, long? expectedVersionCode)
    {
        if (region.QooAppId is not { } appId)
            throw new ArgumentException($"{region.Code} 没有 QooApp 条目");

        var detail = Detail(appId);
        var package = detail.GetProperty("packageId").GetString();
        if (package != region.Package)
            throw new DataException($"QooApp 条目 {appId} 的包名是 {package}，但 {region.Code} 应该是 {region.Package}");
        if (detail.TryGetProperty("isApkReady", out var ready) && ready.ValueKind == JsonValueKind.False)
            throw new SourceNotReadyException($"QooApp 上 {region.Code} 的安装包还没准备好");
        var apk = detail.GetProperty("apk");
        var versionName = apk.GetProperty("versionName").GetString();
        var versionCode = apk.GetProperty("versionCode").GetInt64();
        if (apk.TryGetProperty("dataPackNeeded", out var dataPack) && dataPack.ValueKind == JsonValueKind.True)
            throw new DataException("QooApp 上这个版本需要额外的数据包，暂不支持");
        log?.Invoke($"QooApp {region.Code}：{package} {versionName}（versionCode {versionCode}）");
        if (expectedVersionCode is { } expected && versionCode < expected)
            throw new SourceNotReadyException($"QooApp 上的 versionCode {versionCode} 小于期望的 {expected}，来源还没更新完");

        var dir = Path.Combine(cacheDir, region.Code, $"qooapp-{versionCode}");
        var apks = new List<string>
        {
            _downloader.Download(BaseApkUrl(package!), Path.Combine(dir, "base.apk"),
                apk.TryGetProperty("baseApkMd5", out var md5) ? md5.GetString() : null, userAgent: UserAgent),
        };
        var i = 0;
        foreach (var split in detail.TryGetProperty("splitApks", out var splits) && splits.ValueKind == JsonValueKind.Array
                     ? splits.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            apks.Add(_downloader.Download(new Uri(split.GetProperty("url").GetString()!), Path.Combine(dir, $"split-{i++}.apk"),
                split.TryGetProperty("md5", out var m) ? m.GetString() : null,
                split.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : null, UserAgent));
        }
        PackageSources.PruneSiblings(dir);
        return new DownloadedPackage(Name, apks, versionName, versionCode);
    }

    private static string UserAgent => $"QooApp {ClientVersion}";

    private HttpRequestMessage Request(Uri uri)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Add("X-Version-Name", ClientVersion);
        req.Headers.Add("X-Version-Code", ClientCode);
        req.Headers.Add("X-SDK-Version", "34");
        req.Headers.Add("X-Device-ABIs", "arm64-v8a");
        req.Headers.Add("X-Locale", "en");
        req.Headers.Add("X-System-Locale", "en_US");
        req.Headers.Add("X-User-Token", credentials.Token);
        if (credentials.DeviceId is { Length: > 0 } device)
            req.Headers.Add("X-Device-Id", device);
        return req;
    }

    private JsonElement Detail(int appId)
    {
        using var resp = _downloader.SendOnce(Request(new Uri($"{apiBase}/store/v11/apps/{appId}")), HttpCompletionOption.ResponseContentRead);
        EnsureApiOk(resp, "查询 QooApp 条目");
        var json = JsonDocument.Parse(resp.Content.ReadAsStream()).RootElement;
        if (json.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c is not (0 or 200))
        {
            var message = json.TryGetProperty("message", out var m) ? m.ToString() : "";
            if (c is 401 or 403)
                throw new SourceConfigurationException($"QooApp token 无效或已过期（code {c}）");
            throw new HttpRequestException($"QooApp 返回 code {c}：{message}");
        }
        return json.GetProperty("data");
    }

    private Uri BaseApkUrl(string package)
    {
        var query = "type=app" + (credentials.UserId is { Length: > 0 } user ? "&userId=" + Uri.EscapeDataString(user) : "");
        using var resp = _downloader.SendOnce(Request(new Uri($"{apiBase}/v11/apps/{Uri.EscapeDataString(package)}/download?{query}")));
        if (Downloader.IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
            return location.IsAbsoluteUri ? location : new Uri(new Uri(apiBase), location);
        EnsureApiOk(resp, "获取 QooApp 主包地址");
        throw new HttpRequestException($"QooApp 下载接口没有返回跳转（HTTP {(int)resp.StatusCode}）");
    }

    private static void EnsureApiOk(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var code = (int)resp.StatusCode;
        if (code is 401 or 403)
            throw new SourceConfigurationException($"{what}失败：HTTP {code}，QooApp token 无效或已过期");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new DataException($"{what}失败：HTTP 404");
        throw new HttpRequestException($"{what}失败：HTTP {code}");
    }
}
