using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PjskMetaBuilder.Core.Sources;

/// <summary>下载源的凭据或配置有问题（例如 QooApp token 失效）：重试没用，要人来处理。</summary>
public sealed class SourceConfigurationException(string message) : Exception(message);

/// <summary>
/// 文件下载：手动跟随跳转（便于记录最终地址、便于测试），断点续传（.part），完成后校验大小和 MD5。
/// 网络类失败（超时、5xx、429、内容不完整、MD5 不一致）一律抛 <see cref="HttpRequestException"/>，由调用方决定是否重试。
/// </summary>
public sealed class Downloader(HttpClient http, Action<string>? log = null)
{
    private const int MaxRedirects = 10;

    /// <summary>创建不自动跳转的 HttpClient（跳转由 <see cref="Send"/> 处理）。</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30),
    })
    { Timeout = TimeSpan.FromHours(1) };

    /// <summary>发请求并跟随跳转；返回最终响应（调用方负责释放）。</summary>
    public HttpResponseMessage Send(Func<Uri, HttpRequestMessage> build, Uri uri, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        for (var hop = 0; ; hop++)
        {
            var resp = SendOnce(build(uri), completion);
            if (!IsRedirect(resp.StatusCode))
                return resp;
            var location = resp.Headers.Location;
            resp.Dispose();
            if (location is null)
                throw new HttpRequestException($"{uri.Host} 返回跳转但没有 Location");
            if (hop >= MaxRedirects)
                throw new HttpRequestException($"跳转次数超过 {MaxRedirects}");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException($"跳转到了非 https 地址（{uri.Scheme}://{uri.Host}）");
        }
    }

    public HttpResponseMessage SendOnce(HttpRequestMessage request, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        try
        {
            return http.Send(request, completion);
        }
        catch (TaskCanceledException e)
        {
            throw new HttpRequestException($"请求 {request.RequestUri?.Host} 超时", e);
        }
    }

    public static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    /// <summary>
    /// 下载 <paramref name="uri"/> 到 <paramref name="dest"/>。目标已存在且大小、MD5 符合就跳过；
    /// 有 .part 时用 Range 续传。<paramref name="md5"/>、<paramref name="size"/> 给了就校验。
    /// </summary>
    public string Download(Uri uri, string dest, string? md5 = null, long? size = null, string? userAgent = null)
    {
        if (File.Exists(dest) && (size is null || new FileInfo(dest).Length == size) && (md5 is null || Md5(dest) == md5.ToLowerInvariant()))
        {
            log?.Invoke($"已有 {Path.GetFileName(dest)}，校验一致，跳过下载");
            return dest;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (size is { } total && have > total)
        {
            File.Delete(part);
            have = 0;
        }

        using (var resp = Send(u =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, u);
            req.Headers.UserAgent.ParseAdd(userAgent ?? "pjsk-meta-builder/0.1");
            if (have > 0)
                req.Headers.Range = new RangeHeaderValue(have, null);
            return req;
        }, uri))
        {
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                // .part 已经是完整文件（上次下载完但没来得及改名）或已损坏：交给下面的校验
            }
            else
            {
                EnsureDownloadOk(resp, uri);
                var append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (!append)
                    have = 0;
                log?.Invoke($"下载 {Path.GetFileName(dest)}（{resp.RequestMessage?.RequestUri?.Host ?? uri.Host}）"
                    + (append ? $"，从 {have >> 20} MB 处续传" : ""));
                using var input = resp.Content.ReadAsStream();
                using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write);
                try
                {
                    input.CopyTo(output, 1 << 20);
                }
                catch (IOException e)
                {
                    throw new HttpRequestException($"下载 {Path.GetFileName(dest)} 中断，已保留 .part 下次续传：{e.Message}", e);
                }
            }
        }

        var length = new FileInfo(part).Length;
        if (size is { } expected && length != expected)
        {
            if (length > expected)
                File.Delete(part);
            throw new HttpRequestException($"{Path.GetFileName(dest)} 大小 {length}，应为 {expected}");
        }
        if (md5 is not null && Md5(part) != md5.ToLowerInvariant())
        {
            File.Delete(part);
            throw new HttpRequestException($"{Path.GetFileName(dest)} 的 MD5 不一致，已删除，重试会重新下载");
        }
        File.Move(part, dest, overwrite: true);
        return dest;
    }

    private static void EnsureDownloadOk(HttpResponseMessage resp, Uri uri)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var code = (int)resp.StatusCode;
        var message = $"下载 {uri.Host} 失败：HTTP {code}";
        if (code is 401 or 403)
            throw new SourceConfigurationException(message + "（没有权限，可能是链接或凭据失效）");
        if (code == 404)
            throw new SourceNotReadyException(message + "（文件不存在，可能还没上传完或链接已失效）");
        throw new HttpRequestException(message);
    }

    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }
}
