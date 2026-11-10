using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Sources;
using PjskMetaBuilder.Service;

namespace PjskMetaBuilder.Tests;

public class HmacTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(new string('k', 40));

    [Fact]
    public void SignVerifyRoundTrip()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var body = "{\"a\":1}"u8.ToArray();
        var sig = Hmac.Sign(Secret, now.ToUnixTimeSeconds(), body);
        Assert.StartsWith("sha256=", sig);
        Assert.Null(Hmac.Verify(Secret, now.ToUnixTimeSeconds().ToString(), sig, body, now));
        // 与设计文档的定义逐字一致：HMAC(secret, ts + "." + body)
        var manual = "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes("1800000000.{\"a\":1}")));
        Assert.Equal(manual, sig);
    }

    [Fact]
    public void RejectsTamperingAndStaleTimestamps()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var ts = now.ToUnixTimeSeconds();
        var sig = Hmac.Sign(Secret, ts, "x"u8);
        Assert.NotNull(Hmac.Verify(Secret, ts.ToString(), sig, "y"u8, now));                       // 改了 body
        Assert.NotNull(Hmac.Verify(Encoding.UTF8.GetBytes(new string('z', 40)), ts.ToString(), sig, "x"u8, now)); // 密钥不对
        Assert.NotNull(Hmac.Verify(Secret, ts.ToString(), sig, "x"u8, now.AddMinutes(6)));          // 超出 5 分钟
        Assert.Null(Hmac.Verify(Secret, ts.ToString(), sig, "x"u8, now.AddMinutes(4)));
        Assert.NotNull(Hmac.Verify(Secret, null, sig, "x"u8, now));
        Assert.NotNull(Hmac.Verify(Secret, "abc", sig, "x"u8, now));
    }
}

/// <summary>可控的假流水线：按地区返回结果或抛异常；可以用闸门让任务卡在 running。</summary>
internal sealed class FakeRunner : IJobRunner
{
    public Func<BuildJobRequest, JobOutcome> Behavior { get; set; } = r =>
        new JobOutcome("advanced", "6.4.1", 4647, "abc123", $"{r.Region}/6.4.1", new Dictionary<string, string> { ["meta.json"] = "00" });
    public SemaphoreSlim? Gate { get; set; }
    public ConcurrentQueue<string> Ran { get; } = new();

    public JobOutcome Run(BuildJobRequest request, CancellationToken ct)
    {
        Gate?.Wait(ct);
        Ran.Enqueue(request.RequestId);
        return Behavior(request);
    }
}

public sealed class ServiceTests : IAsyncLifetime
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret-0123456789-0123456789-abc");
    private readonly FakeRunner _runner = new();
    private readonly ConcurrentQueue<(JsonElement Body, bool SignatureOk)> _callbacks = new();
    private int _callbackFailuresLeft;
    private WebApplication _service = null!, _receiver = null!;
    private HttpClient _http = null!;
    private string _callbackUrl = null!;

    public async Task InitializeAsync()
    {
        // 假的同步服务：接收并校验回调签名
        var rb = WebApplication.CreateSlimBuilder();
        rb.WebHost.UseUrls("http://127.0.0.1:0");
        _receiver = rb.Build();
        _receiver.MapPost("/cb", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (Interlocked.Decrement(ref _callbackFailuresLeft) >= 0)
                return Results.StatusCode(500);
            var ok = Hmac.Verify(Secret, ctx.Request.Headers[Hmac.TimestampHeader], ctx.Request.Headers[Hmac.SignatureHeader],
                ms.ToArray(), DateTimeOffset.UtcNow) is null;
            _callbacks.Enqueue((JsonDocument.Parse(ms.ToArray()).RootElement.Clone(), ok));
            return Results.Ok();
        });
        await _receiver.StartAsync();
        _callbackUrl = Address(_receiver) + "/cb";

        var options = new ServiceOptions
        {
            HmacSecret = Secret,
            ListenUrl = "http://127.0.0.1:0",
            CallbackRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
        };
        _service = ServiceHost.Build(options, _runner);
        await _service.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(Address(_service)) };
    }

    public async Task DisposeAsync()
    {
        _runner.Gate?.Release(10);
        await _service.StopAsync();
        await _receiver.StopAsync();
        await _service.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, byte[]? secret = null, long? ts = null)
    {
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body);
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } };
        var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        req.Headers.Add(Hmac.TimestampHeader, t.ToString());
        req.Headers.Add(Hmac.SignatureHeader, Hmac.Sign(secret ?? Secret, t, bytes));
        return await _http.SendAsync(req);
    }

    private object Job(string id, string region = "cn", string? source = null, long? expected = 4647) =>
        new { requestId = id, region, source, expectedVersionCode = expected, callback = _callbackUrl };

    private async Task<JsonElement> WaitCallbackAsync(string requestId)
    {
        for (var i = 0; i < 200; i++)
        {
            var hit = _callbacks.FirstOrDefault(c => c.Body.GetProperty("requestId").GetString() == requestId);
            if (hit.Body.ValueKind != JsonValueKind.Undefined)
            {
                Assert.True(hit.SignatureOk, "回调签名无效");
                return hit.Body;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("没有收到回调");
    }

    [Fact]
    public async Task HealthzNeedsNoSignature()
    {
        var resp = await _http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnsignedOrBadlySignedRequestsAreRejected()
    {
        var unsigned = await _http.PostAsync("/v1/builds", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var wrongKey = await SendAsync(HttpMethod.Post, "/v1/builds", Job("a1"), secret: Encoding.UTF8.GetBytes(new string('x', 40)));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        var stale = await SendAsync(HttpMethod.Post, "/v1/builds", Job("a2"), ts: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Empty(_runner.Ran);
    }

    [Theory]
    [InlineData("bad id!", "cn", null)]
    [InlineData("ok", "xx", null)]
    [InlineData("ok", "cn", "qooapp")]   // cn 没有 QooApp 条目
    [InlineData("ok", "jp", "official")] // jp 没有官网链接
    public async Task InvalidRequestsAre400(string id, string region, string? source)
    {
        var resp = await SendAsync(HttpMethod.Post, "/v1/builds", Job(id, region, source));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RunsJobAndCallsBackWithSignedPayload()
    {
        var resp = await SendAsync(HttpMethod.Post, "/v1/builds", Job("job-1"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var cb = await WaitCallbackAsync("job-1");
        Assert.Equal("succeeded", cb.GetProperty("status").GetString());
        Assert.Equal("advanced", cb.GetProperty("result").GetString());
        Assert.Equal("cn/6.4.1", cb.GetProperty("path").GetString());
        Assert.Equal(4647, cb.GetProperty("versionCode").GetInt64());
        Assert.Equal(JsonValueKind.Null, cb.GetProperty("error").ValueKind);

        var status = await SendAsync(HttpMethod.Get, "/v1/builds/job-1");
        var snap = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("succeeded", snap.GetProperty("status").GetString());
        Assert.Equal("abc123", snap.GetProperty("commit").GetString());
    }

    [Fact]
    public async Task SameRequestIdIsIdempotentAndBusyRegionIs409()
    {
        _runner.Gate = new SemaphoreSlim(0);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/builds", Job("r1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/builds", Job("r1"))).StatusCode);

        var busy = await SendAsync(HttpMethod.Post, "/v1/builds", Job("r2"));
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("r1", JsonDocument.Parse(await busy.Content.ReadAsStringAsync()).RootElement.GetProperty("requestId").GetString());

        // 另一个地区可以排队
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/builds", Job("r3", "jp", expected: null))).StatusCode);

        _runner.Gate.Release(2);
        await WaitCallbackAsync("r1");
        await WaitCallbackAsync("r3");
        Assert.Equal(["r1", "r3"], _runner.Ran.ToArray());   // 串行、按顺序
    }

    [Theory]
    [InlineData("not_ready")]
    [InlineData("network")]
    [InlineData("data")]
    [InlineData("config")]
    [InlineData("internal")]
    public async Task FailuresAreClassifiedInCallback(string kind)
    {
        _runner.Behavior = _ => throw (kind switch
        {
            "not_ready" => new SourceNotReadyException("来源还没更新"),
            "network" => new HttpRequestException("超时"),
            "data" => new DataException("包名不对"),
            "config" => new SourceConfigurationException("token 失效"),
            _ => new InvalidOperationException("bug"),
        });
        await SendAsync(HttpMethod.Post, "/v1/builds", Job("f-" + kind));
        var cb = await WaitCallbackAsync("f-" + kind);
        Assert.Equal("failed", cb.GetProperty("status").GetString());
        Assert.Equal(kind, cb.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task CallbackIsRetriedUntilDelivered()
    {
        _callbackFailuresLeft = 2;
        await SendAsync(HttpMethod.Post, "/v1/builds", Job("retry-1"));
        await WaitCallbackAsync("retry-1");
        for (var i = 0; i < 100; i++)
        {
            var snap = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, "/v1/builds/retry-1")).Content.ReadAsStringAsync()).RootElement;
            if (snap.GetProperty("callback").GetString() == "delivered")
                return;
            await Task.Delay(25);
        }
        Assert.Fail("回调状态没有变成 delivered");
    }

    [Fact]
    public async Task UnknownJobIs404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/builds/nope")).StatusCode);
    }
}
