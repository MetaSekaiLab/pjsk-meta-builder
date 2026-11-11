using System.Net;
using System.Net.Http.Headers;

namespace PjskMetaBuilder.Core.Sources;

/// <summary>
/// 官网下载链接（cn）：短链 → 若干次 302 → CDN 上的单个 APK。HEAD 不可用，用 Range: bytes=0-0 的 GET 拿到最终地址和总大小。
/// 缓存目录按最终文件名区分（文件名里带版本号），同名且大小一致就复用。官网不给 MD5，完整性由大小 + 之后的清单解析保证。
/// 版本号要下载后读清单才知道，所以“来源还没更新”由 MetaBuilder 在读清单时判断。
/// </summary>
public sealed partial class OfficialUrlSource(HttpClient? http = null, Action<string>? log = null) : IPackageSource
{
    private readonly Downloader _downloader = new(http ?? Downloader.CreateClient(), log);

    public string Name => "official";

    /// <summary>探测官网链接：跟随跳转拿到最终地址和文件大小，只取 1 个字节。</summary>
    public (Uri Final, long Total) Probe(RegionInfo region)
    {
        if (region.OfficialUrl is not { } url)
            throw new ArgumentException($"{region.Code} 没有官网下载链接");
        var (final, total) = _downloader.ProbeRange(new Uri(url));
        return (final, total ?? throw new HttpRequestException("官网链接没有返回文件大小"));
    }

    /// <summary>
    /// 只探测版本（§7.5）：最终文件名形如 <c>pjsk_51199071a_v23509_4647_277c_1792657704.apk</c>，
    /// <c>_v&lt;数字&gt;_</c> 后面那段就是 versionCode（2026-11-12 与 app-meta 的 cn 6.4.1 = 4647 对照一致）。
    /// 文件名不符合这个格式时返回 null（调用方只记告警）。
    /// </summary>
    public (Uri Final, long? VersionCode) ProbeVersion(RegionInfo region)
    {
        var (final, _) = Probe(region);
        return (final, VersionCodeFromFileName(Path.GetFileName(final.AbsolutePath)));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"_v\d+_(\d+)_")]
    private static partial System.Text.RegularExpressions.Regex VersionCodePattern();

    public static long? VersionCodeFromFileName(string fileName) =>
        VersionCodePattern().Match(fileName) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var code) ? code : null;

    public DownloadedPackage Fetch(RegionInfo region, string cacheDir, long? expectedVersionCode)
    {
        var (final, total) = Probe(region);

        var fileName = Path.GetFileNameWithoutExtension(final.AbsolutePath);
        if (string.IsNullOrEmpty(fileName) || fileName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            fileName = "apk";
        log?.Invoke($"官网 {region.Code}：{final.Host}/…/{fileName}.apk，{total >> 20} MB");
        var dir = Path.Combine(cacheDir, region.Code, $"official-{fileName}");
        var apk = _downloader.Download(final, Path.Combine(dir, "base.apk"), size: total);
        PackageSources.PruneSiblings(dir);
        return new DownloadedPackage(Name, [apk], null, null);
    }
}
