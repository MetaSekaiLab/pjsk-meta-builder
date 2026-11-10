using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PjskMetaBuilder.Core.Publish;

/// <summary>
/// 用 GitHub App 私钥换 installation token，并**只申请指定仓库**的 contents: write（设计文档 B10）。
/// App 归 StarMoe-org，与同步服务共用；同步服务申请的是 moesekai-masterdata-&lt;region&gt;。
/// </summary>
public sealed class GitHubAppToken(string appId, string privateKeyPem, HttpClient? http = null, string apiBase = "https://api.github.com")
{
    private readonly HttpClient _http = http ?? new HttpClient();

    /// <summary>App 身份的 JWT（RS256，有效 9 分钟，iat 回拨 60 秒容忍时钟偏差）。</summary>
    public string CreateJwt(DateTimeOffset? now = null)
    {
        var t = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = t - 60, exp = t + 540, iss = appId }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{B64Url(signature)}";
    }

    public async Task<string> CreateInstallationTokenAsync(string owner, string repo, CancellationToken ct = default)
    {
        var jwt = CreateJwt();
        using var find = Request(HttpMethod.Get, $"{apiBase}/repos/{owner}/{repo}/installation", jwt);
        using var found = await _http.SendAsync(find, ct);
        await EnsureOk(found, $"查询 {owner}/{repo} 的 App 安装", ct);
        var installationId = (await found.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt64();

        using var create = Request(HttpMethod.Post, $"{apiBase}/app/installations/{installationId}/access_tokens", jwt);
        create.Content = JsonContent.Create(new { repositories = new[] { repo }, permissions = new { contents = "write" } });
        using var created = await _http.SendAsync(create, ct);
        await EnsureOk(created, "申请 installation token", ct);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string jwt)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("pjsk-meta-builder", "0.1"));
        return req;
    }

    private static async Task EnsureOk(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var message = $"{what}失败：HTTP {(int)resp.StatusCode} {(body.Length > 300 ? body[..300] : body)}";
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(message);
        throw new InvalidOperationException(message);
    }

    private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
