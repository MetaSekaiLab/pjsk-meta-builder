namespace PjskMetaBuilder.Core.Sources;

/// <summary>下载到本地的一组安装包（主包 + 拆分包）。版本信息来自下载源，最终以清单为准。</summary>
public sealed record DownloadedPackage(string Source, IReadOnlyList<string> Apks, string? VersionName, long? VersionCode);

public interface IPackageSource
{
    /// <summary>写进 meta.json 的 package.source。</summary>
    string Name { get; }

    /// <summary>
    /// 下载 <paramref name="region"/> 的当前安装包到 <paramref name="cacheDir"/>（按地区分目录，已下载且校验一致的文件直接复用）。
    /// 下载源上的版本比 <paramref name="expectedVersionCode"/> 旧时抛 <see cref="SourceNotReadyException"/>。
    /// </summary>
    DownloadedPackage Fetch(RegionInfo region, string cacheDir, long? expectedVersionCode);
}

public static class PackageSources
{
    /// <summary>默认下载缓存：PJSK_CACHE_DIR，否则 ~/.cache/pjsk-meta-builder。</summary>
    public static string DefaultCacheDir() =>
        Environment.GetEnvironmentVariable("PJSK_CACHE_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "pjsk-meta-builder");

    /// <summary>按名字和地区选下载源；<paramref name="name"/> 为空时用地区的默认来源（有官网链接用官网，否则 QooApp）。</summary>
    public static IPackageSource Create(string? name, RegionInfo region, HttpClient? http = null, Action<string>? log = null)
    {
        name ??= region.OfficialUrl is not null ? "official" : "qooapp";
        return name switch
        {
            "qooapp" => new QooAppSource(QooAppCredentials.FromEnvironment(), http, log),
            "official" => new OfficialUrlSource(http, log),
            _ => throw new ArgumentException($"未知下载源 {name}（可选：qooapp、official；本地文件用 --apk）"),
        };
    }

    /// <summary>删除同一地区、同一来源下其他版本的目录，只保留当前版本（安装包都是几百 MB）。</summary>
    internal static void PruneSiblings(string keepDir)
    {
        var parent = Path.GetDirectoryName(keepDir)!;
        var prefix = Path.GetFileName(keepDir).Split('-')[0] + "-";
        foreach (var dir in Directory.GetDirectories(parent, prefix + "*"))
            if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keepDir), StringComparison.Ordinal))
                Directory.Delete(dir, true);
    }
}
