using System.Net;
using System.Net.Http.Headers;

namespace PjskMetaBuilder.Core.Publish;

/// <summary>GitHub API 请求的公共部分：请求头、错误分类（5xx / 429 / 限流 → 可重试的 HttpRequestException）。</summary>
internal static class GitHubHttp
{
    public static HttpRequestMessage Request(HttpMethod method, string url, string? bearer, string accept = "application/vnd.github+json")
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pjsk-meta-builder", "0.1"));
        return req;
    }

    public static async Task EnsureOk(HttpResponseMessage resp, string what, CancellationToken ct = default)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var message = $"{what}失败：HTTP {(int)resp.StatusCode} {(body.Length > 300 ? body[..300] : body)}";
        var rateLimited = resp.StatusCode == HttpStatusCode.Forbidden
            && resp.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests || rateLimited)
            throw new HttpRequestException(message);
        throw new InvalidOperationException(message);
    }
}
