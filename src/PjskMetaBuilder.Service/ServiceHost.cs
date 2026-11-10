using System.Text.Json;
using PjskMetaBuilder.Core;

namespace PjskMetaBuilder.Service;

/// <summary>
/// HTTP 服务（§7）。只在集群内网提供：
///   POST /v1/builds              发起处理（带签名）→ 202 / 409 / 400 / 401
///   GET  /v1/builds/{requestId}  查状态（带签名，body 为空）
///   GET  /healthz                存活检查（不需要签名，不暴露任务细节）
/// </summary>
public static class ServiceHost
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>构建 WebApplication；<paramref name="runner"/> 为空时用真实流水线（测试可以注入假的）。</summary>
    public static WebApplication Build(ServiceOptions options, IJobRunner? runner = null, HttpClient? callbackHttp = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBodyBytes);
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z' ";
            o.UseUtcTimestamp = true;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new CallbackHttp(callbackHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        builder.Services.AddSingleton(sp => new CallbackSender(sp.GetRequiredService<CallbackHttp>().Client, options,
            sp.GetRequiredService<ILogger<CallbackSender>>()));
        if (runner is not null)
            builder.Services.AddSingleton(runner);
        else
            builder.Services.AddSingleton<IJobRunner, PipelineRunner>();
        builder.Services.AddSingleton<JobQueue>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
        builder.Services.AddSingleton<SelfTestState>();
        if (Environment.GetEnvironmentVariable("PJSK_SELFTEST") == "1")
            builder.Services.AddHostedService<StartupSelfTest>();

        var app = builder.Build();
        var queue = app.Services.GetRequiredService<JobQueue>();
        var selfTest = app.Services.GetRequiredService<SelfTestState>();

        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            version = MetaBuilder.BuilderVersion,
            pending = queue.Pending,
            running = queue.Running is not null,
            selfTest = selfTest.Results is { } r
                ? new { passed = r.Count(x => x.Ok), total = r.Count, failed = r.Where(x => !x.Ok).Select(x => x.Name), at = selfTest.At }
                : null,
        }));

        app.MapPost("/v1/builds", async (HttpContext ctx) =>
        {
            var (body, denied) = await ReadSignedAsync(ctx, options);
            if (denied is not null)
                return denied;

            BuildJobRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<BuildJobRequest>(body, Json);
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "请求体不是合法 JSON" }, statusCode: 400);
            }
            if (request is null)
                return Results.Json(new { error = "请求体为空" }, statusCode: 400);
            if (request.Validate() is { } invalid)
                return Results.Json(new { error = invalid }, statusCode: 400);

            var (status, job) = queue.Enqueue(request);
            return status switch
            {
                EnqueueStatus.Accepted => Results.Json(new { requestId = job.Request.RequestId, status = "queued" }, statusCode: 202),
                EnqueueStatus.Duplicate => Results.Json(new { requestId = job.Request.RequestId, status = job.Status.ToString().ToLowerInvariant() }, statusCode: 202),
                _ => Results.Json(new { error = $"{request.Region} 已有任务", requestId = job.Request.RequestId, status = job.Status.ToString().ToLowerInvariant() }, statusCode: 409),
            };
        });

        app.MapGet("/v1/builds/{requestId}", async (HttpContext ctx, string requestId) =>
        {
            var (_, denied) = await ReadSignedAsync(ctx, options);
            if (denied is not null)
                return denied;
            return queue.Get(requestId) is { } job
                ? Results.Json(job.Snapshot(), Json)
                : Results.Json(new { error = "没有这个任务（可能已过期或服务重启过）" }, statusCode: 404);
        });

        return app;
    }

    /// <summary>读取原始请求体并校验签名；不通过时返回 401（不告诉调用方具体原因）。</summary>
    private static async Task<(byte[] Body, IResult? Denied)> ReadSignedAsync(HttpContext ctx, ServiceOptions options)
    {
        using var buffer = new MemoryStream();
        try
        {
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            return ([], Results.Json(new { error = "请求体过大" }, statusCode: 413));
        }
        var body = buffer.ToArray();
        var reason = Hmac.Verify(options.HmacSecret,
            ctx.Request.Headers[Hmac.TimestampHeader].FirstOrDefault(),
            ctx.Request.Headers[Hmac.SignatureHeader].FirstOrDefault(),
            body, DateTimeOffset.UtcNow);
        if (reason is null)
            return (body, null);
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth")
            .LogWarning("拒绝 {Method} {Path}：{Reason}", ctx.Request.Method, ctx.Request.Path, reason);
        return (body, Results.Json(new { error = "签名无效" }, statusCode: 401));
    }

    /// <summary>包一层，避免把裸 HttpClient 注册成全局单例。</summary>
    private sealed record CallbackHttp(HttpClient Client);
}
