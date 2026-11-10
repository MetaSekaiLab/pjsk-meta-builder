using PjskMetaBuilder.Core.Util;

namespace PjskMetaBuilder.Core.Publish;

public enum PublishOutcome
{
    /// <summary>写入版本目录并更新 latest.json。</summary>
    Advanced,
    /// <summary>补写一个旧版本的目录，latest.json 不动。</summary>
    SnapshotOnly,
    /// <summary>什么都不改（已经发布过同一个构建）。</summary>
    Noop,
    /// <summary>同版本、不同 appHash，但 versionCode 不比 latest 新：拒绝。</summary>
    Rejected,
}

/// <summary>用于判定的最小信息：来自 meta.json / latest.json。</summary>
public sealed record VersionIdentity(string AppVersion, string AppHash, long VersionCode);

public sealed record UpdatePlan(PublishOutcome Outcome, string? Directory, bool UpdateLatest, string Reason);

/// <summary>
/// 设计文档 §4.5 的更新规则。纯函数：输入当前 latest、新构建、仓库里已存在的版本目录，输出要做什么。
/// 已存在的版本目录永不改写：目录在且身份相同就跳过写目录；目录在但身份不同交给调用方报错。
/// </summary>
public static class UpdatePlanner
{
    public static string DirectoryName(VersionIdentity v, bool rebuild) =>
        rebuild ? $"{v.AppVersion}+{v.VersionCode}" : v.AppVersion;

    public static UpdatePlan Plan(VersionIdentity? latest, VersionIdentity build)
    {
        if (latest is null)
            return new(PublishOutcome.Advanced, DirectoryName(build, false), true, "该地区还没有 latest");

        var cmp = AppVersion.Compare(build.AppVersion, latest.AppVersion);
        if (cmp > 0)
            return new(PublishOutcome.Advanced, DirectoryName(build, false), true,
                $"appVersion {latest.AppVersion} → {build.AppVersion}");
        if (cmp < 0)
            return new(PublishOutcome.SnapshotOnly, DirectoryName(build, false), false,
                $"appVersion {build.AppVersion} 比 latest {latest.AppVersion} 旧，只补版本目录");
        if (string.Equals(build.AppHash, latest.AppHash, StringComparison.OrdinalIgnoreCase))
            return new(PublishOutcome.Noop, null, false, "与 latest 是同一个构建");
        if (build.VersionCode > latest.VersionCode)
            return new(PublishOutcome.Advanced, DirectoryName(build, true), true,
                $"同版本 {build.AppVersion} 的新构建：versionCode {latest.VersionCode} → {build.VersionCode}");
        return new(PublishOutcome.Rejected, null, false,
            $"同版本 {build.AppVersion}、appHash 不同，但 versionCode {build.VersionCode} 不比 latest 的 {latest.VersionCode} 新");
    }
}
