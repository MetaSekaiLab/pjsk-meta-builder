using System.Net;
using System.Net.Http.Headers;

namespace PjskMetaBuilder.Core.Sources;

/// <summary>
/// 官网下载链接（cn）：短链 → 若干次 302 → CDN 上的单个 APK。HEAD 不可用，用 Range: bytes=0-0 的 GET 拿到最终地址和总大小。
/// 缓存目录按最终文件名区分（文件名里带版本号），同名且大小一致就复用。官网不给 MD5，完整性由大小 + 之后的清单解析保证。
/// 版本号要下载后读清单才知道，所以“来源还没更新”由 MetaBuilder 在读清单时判断。
/// </summary>
public sealed class OfficialUrlSource(HttpClient? http = null, Action<string>? log = null) : IPackageSource
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
