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

    public DownloadedPackage Fetch(RegionInfo region, string cacheDir, long? expectedVersionCode)
    {
        if (region.OfficialUrl is not { } url)
            throw new ArgumentException($"{region.Code} 没有官网下载链接");

        Uri final;
        long total;
        using (var probe = _downloader.Send(u =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd("pjsk-meta-builder/0.1");
            req.Headers.Range = new RangeHeaderValue(0, 0);
            return req;
        }, new Uri(url)))
        {
            if (!probe.IsSuccessStatusCode)
            {
                var code = (int)probe.StatusCode;
                if (code == 404)
                    throw new SourceNotReadyException($"官网链接返回 404（{probe.RequestMessage?.RequestUri?.Host}）");
                throw new HttpRequestException($"官网链接返回 HTTP {code}");
            }
            final = probe.RequestMessage!.RequestUri!;
            total = probe.StatusCode == HttpStatusCode.PartialContent && probe.Content.Headers.ContentRange?.Length is { } len ? len
                : probe.Content.Headers.ContentLength ?? throw new HttpRequestException("官网链接没有返回文件大小");
        }

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
