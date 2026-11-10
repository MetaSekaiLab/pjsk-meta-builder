using System.Text.Json;

namespace PjskMetaBuilder.Core.Publish;

public sealed record PublishRequest(
    string Region,
    string BuildDir,
    string Repo = "StarMoe-org/moesekai-app-meta",
    string Branch = "main",
    string? Token = null,                // GitHub installation token；dry-run 可以不给（公开仓库匿名读）
    bool DryRun = false,
    string CommitterName = PublishRequest.BotName,   // 仅本地裸仓库使用；GitHub API 提交固定是 App 的 bot
    string CommitterEmail = PublishRequest.BotEmail,
    int MaxAttempts = 5,
    TimeSpan? RetryBaseDelay = null)
{
    /// <summary>GitHub App moesekai-bot 的 bot 账号（App ID 5693240，bot 用户 id 354789470），提交会关联到该 App。</summary>
    public const string BotName = "moesekai-bot[bot]";
    public const string BotEmail = "354789470+moesekai-bot[bot]@users.noreply.github.com";
}

public sealed record PublishResult(
    string Result,              // advanced / snapshot_only / noop / rejected（dry-run 时同样给出判定）
    bool DryRun,
    string Reason,
    string? Path,               // 写入的版本目录，例：cn/6.4.1
    string? Commit,
    string? OldAppVersion,
    IReadOnlyDictionary<string, string> Files);

/// <summary>
/// 设计文档 §5.8：读分支头 → 在该提交上读 latest.json → 按 §4.5 判定 → 一个提交写入全部文件。
/// 提交带“期望的父提交”，分支头已移动（别人先提交了）就重新读、重新判定，最多 MaxAttempts 次。
/// dry-run 时判定完全相同，但不提交（GitHub 公开仓库可以匿名读）。
/// </summary>
public static class Publisher
{
    public static PublishResult Publish(PublishRequest request, IMetaRepository? repository = null)
    {
        var region = Regions.Get(request.Region);
        var files = ContentLinter.AllowedFiles
            .Where(n => File.Exists(System.IO.Path.Combine(request.BuildDir, n)))
            .ToDictionary(n => n, n => File.ReadAllBytes(System.IO.Path.Combine(request.BuildDir, n)));
        var meta = ContentLinter.CheckDirectory(files, region.Nuverse);
        if (meta.GetProperty("region").GetString() != region.Code)
            throw new DataException($"meta.json 的 region 是 {meta.GetProperty("region").GetString()}，不是 {region.Code}");
        if (!request.DryRun && repository is null && string.IsNullOrEmpty(request.Token) && Git.IsGitHub(request.Repo))
            throw new ArgumentException("没有写入凭据：需要 GitHub App（或用 --dry-run）");

        var repo = repository ?? MetaRepositories.Open(request);
        var build = Identity(meta);
        var baseDelay = request.RetryBaseDelay ?? TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            var outcome = Attempt(request, region.Code, files, build, repo);
            if (outcome is not null)
                return outcome;
            if (attempt >= request.MaxAttempts)
                throw new HttpRequestException($"连续 {attempt} 次提交时分支已被别人更新");
            Thread.Sleep(TimeSpan.FromTicks(Math.Min(baseDelay.Ticks << attempt, TimeSpan.FromSeconds(20).Ticks)));
        }
    }

    /// <summary>返回 null 表示分支头已移动，需要重来。</summary>
    private static PublishResult? Attempt(PublishRequest request, string region, Dictionary<string, byte[]> files,
        VersionIdentity build, IMetaRepository repo)
    {
        var head = repo.Head();
        var latestBytes = head is null ? null : repo.ReadFile(head, $"{region}/latest.json");
        VersionIdentity? latest = latestBytes is null ? null : Identity(JsonDocument.Parse(latestBytes).RootElement);
        var plan = UpdatePlanner.Plan(latest, build);
        var hashes = files.ToDictionary(kv => kv.Key, kv => Output.Artifacts.Sha256Hex(kv.Value));

        if (plan.Outcome is PublishOutcome.Noop or PublishOutcome.Rejected)
            return Result(plan, request, null, null, latest, hashes);

        var relDir = $"{region}/{plan.Directory}";
        var writes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var existingBytes = head is null ? null : repo.ReadFile(head, $"{relDir}/meta.json");
        if (existingBytes is not null)
        {
            // 已发布的目录永不改写：身份相同就视为已发布；不同就报错
            var existing = Identity(JsonDocument.Parse(existingBytes).RootElement);
            if (existing != build)
                throw new DataException($"{relDir} 已经存在，但内容属于另一个构建（{existing}）；已发布的目录不能修改");
        }
        else
        {
            foreach (var (name, bytes) in files)
                writes[$"{relDir}/{name}"] = bytes;
        }
        if (plan.UpdateLatest)
            writes[$"{region}/latest.json"] = files["meta.json"];
        if (writes.Count == 0)
            return Result(plan with { Outcome = PublishOutcome.Noop, Reason = plan.Reason + "（版本目录已存在）" },
                request, relDir, null, latest, hashes);
        if (request.DryRun)
            return Result(plan, request, relDir, null, latest, hashes);

        var message = plan.Outcome == PublishOutcome.SnapshotOnly
            ? $"{region}: 补充 {build.AppVersion} (versionCode {build.VersionCode})"
            : $"{region}: {latest?.AppVersion ?? "∅"} → {plan.Directory} (versionCode {build.VersionCode})";
        var commit = repo.Commit(head, writes, message);
        return commit is null ? null : Result(plan, request, relDir, commit, latest, hashes);
    }

    private static VersionIdentity Identity(JsonElement meta) => new(
        meta.GetProperty("appVersion").GetString()!,
        meta.GetProperty("appHash").GetString()!,
        meta.GetProperty("package").GetProperty("versionCode").GetInt64());

    private static PublishResult Result(UpdatePlan plan, PublishRequest request, string? path, string? commit,
        VersionIdentity? latest, Dictionary<string, string> hashes) => new(
        plan.Outcome switch
        {
            PublishOutcome.Advanced => "advanced",
            PublishOutcome.SnapshotOnly => "snapshot_only",
            PublishOutcome.Noop => "noop",
            _ => "rejected",
        },
        request.DryRun, plan.Reason, path, commit, latest?.AppVersion, hashes);
}
