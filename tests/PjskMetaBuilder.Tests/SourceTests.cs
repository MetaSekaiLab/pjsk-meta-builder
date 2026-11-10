using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Sources;

namespace PjskMetaBuilder.Tests;

/// <summary>按 URL 路由的假 HTTP 服务；记录所有请求。</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var resp = _route(request);
        resp.RequestMessage = request;
        return resp;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(Send(request, ct));

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    /// <summary>支持 Range 的文件响应。</summary>
    public static HttpResponseMessage File(HttpRequestMessage req, byte[] data)
    {
        if (req.Headers.Range?.Ranges.FirstOrDefault() is { } range)
        {
            var from = range.From ?? 0;
            var to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            if (from >= data.Length)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            var slice = data[(int)from..((int)to + 1)];
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
            return partial;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    }
}

public class QooAppSourceTests
{
    private static readonly byte[] Base = RandomNumberGenerator.GetBytes(3000);
    private static readonly byte[] Split = RandomNumberGenerator.GetBytes(2000);
    private static string Md5(byte[] b) => Convert.ToHexStringLower(MD5.HashData(b));

    private static string NewCache() => Path.Combine(Path.GetTempPath(), "pmb-cache", Guid.NewGuid().ToString("N"));

    private static string Detail(string package = "com.sega.ColorfulStage.en", long versionCode = 109, string? baseMd5 = null) =>
        "{\"code\":200,\"data\":{\"packageId\":\"" + package + "\",\"isApkReady\":true,"
        + "\"apk\":{\"versionName\":\"6.1.0\",\"versionCode\":" + versionCode + ",\"dataPackNeeded\":false,\"baseApkMd5\":\"" + (baseMd5 ?? Md5(Base)) + "\"},"
        + "\"splitApks\":[{\"url\":\"https://cdn.test/split.apk\",\"md5\":\"" + Md5(Split) + "\",\"size\":" + Split.Length + "}]}}";

    private static (QooAppSource Source, FakeHttp Http) Create(Func<HttpRequestMessage, HttpResponseMessage>? detailRoute = null,
        Func<HttpRequestMessage, HttpResponseMessage>? baseRoute = null)
    {
        var http = new FakeHttp(req => req.RequestUri!.AbsolutePath switch
        {
            "/store/v11/apps/18337" => (detailRoute ?? (_ => FakeHttp.Json(Detail())))(req),
            "/v11/apps/com.sega.ColorfulStage.en/download" => FakeHttp.Redirect("https://cdn.test/base.apk"),
            "/base.apk" => (baseRoute ?? (r => FakeHttp.File(r, Base)))(req),
            "/split.apk" => FakeHttp.File(req, Split),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        return (new QooAppSource(new QooAppCredentials("tkn"), new HttpClient(http), apiBase: "https://api.test"), http);
    }

    [Fact]
    public void DownloadsBaseAndSplitsAndVerifies()
    {
        var (source, http) = Create();
        var cache = NewCache();
        var pkg = source.Fetch(Regions.Get("en"), cache, expectedVersionCode: 109);

        Assert.Equal(("qooapp", "6.1.0", 109L), (pkg.Source, pkg.VersionName, pkg.VersionCode));
        Assert.Equal([Path.Combine(cache, "en", "qooapp-109", "base.apk"), Path.Combine(cache, "en", "qooapp-109", "split-0.apk")], pkg.Apks);
        Assert.Equal(Base, File.ReadAllBytes(pkg.Apks[0]));
        Assert.Equal(Split, File.ReadAllBytes(pkg.Apks[1]));
        Assert.Equal("tkn", http.Requests[0].Headers.GetValues("X-User-Token").Single());
        // 下载地址不带 token
        Assert.All(http.Requests.Where(r => r.RequestUri!.Host == "cdn.test"), r => Assert.False(r.Headers.Contains("X-User-Token")));
    }

    [Fact]
    public void ReusesVerifiedFilesAndPrunesOlderVersions()
    {
        var cache = NewCache();
        Directory.CreateDirectory(Path.Combine(cache, "en", "qooapp-100"));
        Create().Source.Fetch(Regions.Get("en"), cache, null);
        var (source, http) = Create();
        source.Fetch(Regions.Get("en"), cache, null);

        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Host == "cdn.test");
        Assert.False(Directory.Exists(Path.Combine(cache, "en", "qooapp-100")));
    }

    [Fact]
    public void ResumesFromPartFile()
    {
        var cache = NewCache();
        var dir = Path.Combine(cache, "en", "qooapp-109");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "base.apk.part"), Base[..1000]);
        var (source, http) = Create();
        var pkg = source.Fetch(Regions.Get("en"), cache, null);

        Assert.Equal(Base, File.ReadAllBytes(pkg.Apks[0]));
        var baseReq = http.Requests.Single(r => r.RequestUri!.AbsolutePath == "/base.apk");
        Assert.Equal(1000, baseReq.Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public void Md5MismatchIsRetryableAndDiscardsFile()
    {
        var cache = NewCache();
        var (source, _) = Create(detailRoute: _ => FakeHttp.Json(Detail(baseMd5: new string('0', 32))));
        Assert.Throws<HttpRequestException>(() => source.Fetch(Regions.Get("en"), cache, null));
        Assert.False(File.Exists(Path.Combine(cache, "en", "qooapp-109", "base.apk.part")));
        Assert.False(File.Exists(Path.Combine(cache, "en", "qooapp-109", "base.apk")));
    }

    [Fact]
    public void OlderThanExpectedFailsBeforeDownloading()
    {
        var (source, http) = Create();
        Assert.Throws<SourceNotReadyException>(() => source.Fetch(Regions.Get("en"), NewCache(), expectedVersionCode: 110));
        Assert.Single(http.Requests);
    }

    [Fact]
    public void WrongPackageIsDataError()
    {
        var (source, _) = Create(detailRoute: _ => FakeHttp.Json(Detail(package: "com.example")));
        Assert.Throws<DataException>(() => source.Fetch(Regions.Get("en"), NewCache(), null));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":401,"message":"unauthorized"}""")]
    [InlineData(HttpStatusCode.OK, """{"code":401,"message":"token expired"}""")]
    public void InvalidTokenIsConfigurationError(HttpStatusCode status, string body)
    {
        var (source, _) = Create(detailRoute: _ => FakeHttp.Json(body, status));
        Assert.Throws<SourceConfigurationException>(() => source.Fetch(Regions.Get("en"), NewCache(), null));
    }

    [Fact]
    public void ServerErrorIsRetryable()
    {
        var (source, _) = Create(baseRoute: _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Throws<HttpRequestException>(() => source.Fetch(Regions.Get("en"), NewCache(), null));
    }
}

public class OfficialUrlSourceTests
{
    private static readonly byte[] Apk = RandomNumberGenerator.GetBytes(5000);

    private static (OfficialUrlSource Source, FakeHttp Http) Create(string file = "pjsk_v23509_4647.apk")
    {
        var http = new FakeHttp(req => (req.RequestUri!.Host, req.RequestUri.AbsolutePath) switch
        {
            ("pjskyy.ugurl.cn", _) => FakeHttp.Redirect("https://hop.test/x"),
            ("hop.test", _) => FakeHttp.Redirect($"https://cdn.test/package/{file}?v=1"),
            ("cdn.test", _) => FakeHttp.File(req, Apk),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        return (new OfficialUrlSource(new HttpClient(http)), http);
    }

    [Fact]
    public void FollowsRedirectsAndDownloadsWithSizeCheck()
    {
        var cache = Path.Combine(Path.GetTempPath(), "pmb-cache", Guid.NewGuid().ToString("N"));
        var (source, http) = Create();
        var pkg = source.Fetch(Regions.Get("cn"), cache, null);

        Assert.Equal(Path.Combine(cache, "cn", "official-pjsk_v23509_4647", "base.apk"), Assert.Single(pkg.Apks));
        Assert.Equal(Apk, File.ReadAllBytes(pkg.Apks[0]));

        // 同一个文件再来一次：只探测，不下载
        var (again, http2) = Create();
        again.Fetch(Regions.Get("cn"), cache, null);
        Assert.Single(http2.Requests, r => r.RequestUri!.Host == "cdn.test");

        // 新版本：下载到新目录，旧目录删除
        var (newer, _) = Create("pjsk_v23600_4700.apk");
        newer.Fetch(Regions.Get("cn"), cache, null);
        Assert.False(Directory.Exists(Path.Combine(cache, "cn", "official-pjsk_v23509_4647")));
        Assert.True(File.Exists(Path.Combine(cache, "cn", "official-pjsk_v23600_4700", "base.apk")));
    }

    [Fact]
    public void RefusesDowngradeToHttp()
    {
        var http = new FakeHttp(req => req.RequestUri!.Host == "pjskyy.ugurl.cn"
            ? FakeHttp.Redirect("http://cdn.test/a.apk")
            : FakeHttp.File(req, Apk));
        var source = new OfficialUrlSource(new HttpClient(http));
        Assert.Throws<HttpRequestException>(() => source.Fetch(Regions.Get("cn"), Path.GetTempPath(), null));
    }
}
