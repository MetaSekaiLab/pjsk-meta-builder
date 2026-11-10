using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Sources;

namespace PjskMetaBuilder.Service;

/// <summary>POST /v1/builds 的请求体（§7.1）。</summary>
public sealed record BuildJobRequest(
    string RequestId,
    string Region,
    string? Source,
    long? ExpectedVersionCode,
    string Callback)
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9._:-]{1,64}$", RegexOptions.Compiled);

    /// <summary>校验通过返回 null，否则返回给调用方的错误说明。</summary>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(RequestId) || !IdPattern.IsMatch(RequestId))
            return "requestId 只能是 1–64 位字母、数字或 ._:-";
        if (!Regions.All.ContainsKey(Region ?? ""))
            return $"未知地区（可选：{string.Join(", ", Regions.All.Keys)}）";
        if (Source is not null and not ("qooapp" or "official"))
            return "source 只能是 qooapp 或 official";
        if (Source == "qooapp" && Regions.All[Region!].QooAppId is null)
            return $"{Region} 没有 QooApp 条目";
        if (Source == "official" && Regions.All[Region!].OfficialUrl is null)
            return $"{Region} 没有官网链接";
        if (ExpectedVersionCode is <= 0)
            return "expectedVersionCode 必须是正整数";
        if (!Uri.TryCreate(Callback, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "callback 必须是 http(s) 绝对地址";
        return null;
    }
}

public enum JobStatus { Queued, Running, Succeeded, Failed }

/// <summary>失败分类（§7.2 的 error.kind）：调用方据此决定重试、稍后再来还是告警。</summary>
public static class ErrorKinds
{
    public const string Network = "network";     // 可重试
    public const string NotReady = "not_ready";  // 下载源还没更新，稍后再发起
    public const string Data = "data";           // 输入有问题，重试没用
    public const string Config = "config";       // 凭据/配置问题，要人处理
    public const string Internal = "internal";

    public static string Classify(Exception e) => e switch
    {
        SourceNotReadyException => NotReady,
        SourceConfigurationException => Config,
        DataException => Data,
        HttpRequestException or TimeoutException => Network,
        _ => Internal,
    };
}

public sealed record JobError(string Kind, string Message);

/// <summary>流水线成功跑完的结果（发布判定可能是 rejected，也算跑完）。</summary>
public sealed record JobOutcome(
    string Result,
    string AppVersion,
    long VersionCode,
    string? Commit,
    string? Path,
    IReadOnlyDictionary<string, string> Files);

public sealed class BuildJob(BuildJobRequest request)
{
    public BuildJobRequest Request { get; } = request;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public JobOutcome? Outcome { get; set; }
    public JobError? Error { get; set; }
    public string? CallbackState { get; set; }   // pending / delivered / gave_up

    public object Snapshot() => new
    {
        requestId = Request.RequestId,
        region = Request.Region,
        status = Status.ToString().ToLowerInvariant(),
        createdAt = CreatedAt,
        startedAt = StartedAt,
        finishedAt = FinishedAt,
        result = Outcome?.Result,
        appVersion = Outcome?.AppVersion,
        versionCode = Outcome?.VersionCode,
        commit = Outcome?.Commit,
        path = Outcome?.Path,
        error = Error,
        callback = CallbackState,
    };
}

/// <summary>回调体（§7.2）。</summary>
public sealed record CallbackPayload(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("appVersion")] string? AppVersion,
    [property: JsonPropertyName("versionCode")] long? VersionCode,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("files")] IReadOnlyDictionary<string, string>? Files,
    [property: JsonPropertyName("error")] JobError? Error)
{
    public static CallbackPayload From(BuildJob job) => new(
        job.Request.RequestId,
        job.Request.Region,
        job.Status == JobStatus.Succeeded ? "succeeded" : "failed",
        job.Outcome?.Result,
        job.Outcome?.AppVersion,
        job.Outcome?.VersionCode,
        job.Outcome?.Commit,
        job.Outcome?.Path,
        job.Outcome?.Files,
        job.Error);
}
