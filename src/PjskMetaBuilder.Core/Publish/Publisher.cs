using System.Text.Json;

namespace PjskMetaBuilder.Core.Publish;

public sealed record PublishRequest(
    string Region,
    string BuildDir,
    string Repo = "StarMoe-org/moesekai-app-meta",
    string Branch = "main",
    string? Token = null,
    bool DryRun = false,
    string CommitterName = "pjsk-meta-builder",
    string CommitterEmail = "pjsk-meta-builder@users.noreply.github.com",
    int MaxAttempts = 5);

public sealed record PublishResult(
    string Result,              // advanced / snapshot_only / noop / rejected（dry-run 时同样给出判定）
    bool DryRun,
    string Reason,
    string? Path,               // 写入的版本目录，例：cn/6.4.1
    string? Commit,
    string? OldAppVersion,
    IReadOnlyDictionary<string, string> Files);

/// <summary>
/// 设计文档 §5.8：浅克隆 → 按 §4.5 判定 → 写文件 → 一次提交 → push；push 被拒时重新克隆、重新判定。
/// dry-run 时匿名克隆（公开仓库），判定完全相同，但不提交、不推送。
/// </summary>
public static class Publisher
{
    public static PublishResult Publish(PublishRequest request)
    {
        var region = Regions.Get(request.Region);
        var files = ContentLinter.AllowedFiles
            .Where(n => File.Exists(System.IO.Path.Combine(request.BuildDir, n)))
            .ToDictionary(n => n, n => File.ReadAllBytes(System.IO.Path.Combine(request.BuildDir, n)));
        var meta = ContentLinter.CheckDirectory(files, region.Nuverse);
        if (meta.GetProperty("region").GetString() != region.Code)
            throw new DataException($"meta.json 的 region 是 {meta.GetProperty("region").GetString()}，不是 {region.Code}");
        if (!request.DryRun && string.IsNullOrEmpty(request.Token) && Git.IsGitHub(request.Repo))
            throw new ArgumentException("没有写入凭据：需要 GitHub App（或用 --dry-run）");

        var build = Identity(meta);
        var git = new Git(request.DryRun ? null : request.Token, request.CommitterName, request.CommitterEmail);
        for (var attempt = 1; ; attempt++)
        {
            var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pjsk-meta-publish", Guid.NewGuid().ToString("N"));
            try
            {
                var outcome = Attempt(request, region.Code, files, build, git, work);
                if (outcome is not null)
                    return outcome;
            }
            finally
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, true);
            }
            if (attempt >= request.MaxAttempts)
                throw new HttpRequestException($"连续 {attempt} 次 push 被拒绝");
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 20)));
        }
    }

    /// <summary>返回 null 表示 push 被拒（远端有新提交），需要重来。</summary>
    private static PublishResult? Attempt(PublishRequest request, string region, Dictionary<string, byte[]> files,
        VersionIdentity build, Git git, string work)
    {
        CloneOrInit(git, request, work);

        var latestPath = System.IO.Path.Combine(work, region, "latest.json");
        VersionIdentity? latest = File.Exists(latestPath)
            ? Identity(JsonDocument.Parse(File.ReadAllBytes(latestPath)).RootElement)
            : null;
        var plan = UpdatePlanner.Plan(latest, build);
        var hashes = files.ToDictionary(kv => kv.Key, kv => Output.Artifacts.Sha256Hex(kv.Value));

        if (plan.Outcome is PublishOutcome.Noop or PublishOutcome.Rejected)
            return Result(plan, request, null, null, latest, hashes);

        var relDir = $"{region}/{plan.Directory}";
        var dir = System.IO.Path.Combine(work, region, plan.Directory!);
        var writes = new List<string>();
        if (Directory.Exists(dir))
        {
            // 已发布的目录永不改写：身份相同就视为已发布；不同就报错
            var existingMeta = System.IO.Path.Combine(dir, "meta.json");
            var existing = File.Exists(existingMeta) ? Identity(JsonDocument.Parse(File.ReadAllBytes(existingMeta)).RootElement) : null;
            if (existing != build)
                throw new DataException($"{relDir} 已经存在，但内容属于另一个构建（{existing}）；已发布的目录不能修改");
        }
        else
        {
            Directory.CreateDirectory(dir);
            foreach (var (name, bytes) in files)
            {
                File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);
                writes.Add($"{relDir}/{name}");
            }
        }
        if (plan.UpdateLatest)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(latestPath)!);
            File.WriteAllBytes(latestPath, files["meta.json"]);
            writes.Add($"{region}/latest.json");
        }
        if (writes.Count == 0)
            return Result(plan with { Outcome = PublishOutcome.Noop, Reason = plan.Reason + "（版本目录已存在）" },
                request, relDir, null, latest, hashes);
        if (request.DryRun)
            return Result(plan, request, relDir, null, latest, hashes);

        git.Check(work, ["add", "--", .. writes]);
        var message = plan.Outcome == PublishOutcome.SnapshotOnly
            ? $"{region}: 补充 {build.AppVersion} (versionCode {build.VersionCode})"
            : $"{region}: {latest?.AppVersion ?? "∅"} → {plan.Directory} (versionCode {build.VersionCode})";
        git.Check(work, "commit", "-q", "-m", message);
        var (code, _, err) = git.Run(work, "push", "-q", "origin", $"HEAD:{request.Branch}");
        if (code != 0)
        {
            if (err.Contains("rejected") || err.Contains("fetch first") || err.Contains("non-fast-forward"))
                return null;
            throw new GitException($"git push 失败：{Git.Trim(err)}", err);
        }
        var commit = git.Check(work, "rev-parse", "HEAD").Trim();
        return Result(plan, request, relDir, commit, latest, hashes);
    }

    private static void CloneOrInit(Git git, PublishRequest request, string work)
    {
        var url = Git.RemoteUrl(request.Repo);
        var (code, heads, err) = git.Run(null, "ls-remote", "--heads", url, request.Branch);
        if (code != 0)
            throw new HttpRequestException($"读取产物仓库失败：{Git.Trim(err)}");
        if (heads.Trim().Length > 0)
        {
            git.Check(null, "clone", "-q", "--depth", "1", "--branch", request.Branch, url, work);
            return;
        }
        // 空仓库（第一次发布）
        Directory.CreateDirectory(work);
        git.Check(work, "init", "-q", "-b", request.Branch);
        git.Check(work, "remote", "add", "origin", url);
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
