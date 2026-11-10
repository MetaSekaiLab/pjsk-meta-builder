using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PjskMetaBuilder.Core.Publish;

/// <summary>
/// 通过 GitHub API 读写产物仓库，不克隆、不用 git 命令行。
/// 读：REST contents（raw），固定在某个提交上读，判定和写入看到的是同一个版本。
/// 写：GraphQL createCommitOnBranch。一次调用 = 一个提交，带 expectedHeadOid 做乐观并发；
/// 提交由 GitHub 以 App 的 bot 身份创建并签名（Verified），作者不能也不需要指定。
/// 限制：不能在空仓库上创建第一个提交（分支必须已存在）。
/// </summary>
public sealed class GitHubApiRepository(string owner, string name, string branch, string? token,
    HttpClient? http = null, string apiBase = "https://api.github.com") : IMetaRepository
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

    public static GitHubApiRepository FromSlug(string slug, string branch, string? token)
    {
        var parts = slug.Split('/');
        return new GitHubApiRepository(parts[0], parts[1], branch, token);
    }

    private string RepoUrl => $"{apiBase}/repos/{owner}/{name}";

    public string? Head()
    {
        using var req = GitHubHttp.Request(HttpMethod.Get, $"{RepoUrl}/git/ref/heads/{Uri.EscapeDataString(branch)}", token);
        using var resp = _http.Send(req);
        // 404：分支不存在；409：仓库为空
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return null;
        GitHubHttp.EnsureOk(resp, $"读取 {owner}/{name} 的 {branch} 分支").GetAwaiter().GetResult();
        var json = resp.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        return json.GetProperty("object").GetProperty("sha").GetString();
    }

    public byte[]? ReadFile(string commit, string path)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var req = GitHubHttp.Request(HttpMethod.Get, $"{RepoUrl}/contents/{escaped}?ref={commit}", token,
            accept: "application/vnd.github.raw+json");
        using var resp = _http.Send(req);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        GitHubHttp.EnsureOk(resp, $"读取 {path}").GetAwaiter().GetResult();
        return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    public string? Commit(string? parent, IReadOnlyDictionary<string, byte[]> files, string message)
    {
        if (parent is null)
            throw new InvalidOperationException($"{owner}/{name} 没有 {branch} 分支：GitHub API 不能在空仓库上创建第一个提交，先在仓库里建好 {branch}");
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("通过 GitHub API 提交需要 installation token");

        const string mutation = """
            mutation($input: CreateCommitOnBranchInput!) {
              createCommitOnBranch(input: $input) { commit { oid } }
            }
            """;
        var input = new
        {
            branch = new { repositoryNameWithOwner = $"{owner}/{name}", branchName = branch },
            message = new { headline = message },
            expectedHeadOid = parent,
            fileChanges = new
            {
                additions = files.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { path = kv.Key, contents = Convert.ToBase64String(kv.Value) }).ToArray(),
            },
        };
        using var req = GitHubHttp.Request(HttpMethod.Post, $"{apiBase}/graphql", token);
        req.Content = JsonContent.Create(new { query = mutation, variables = new { input } });
        using var resp = _http.Send(req);
        GitHubHttp.EnsureOk(resp, "创建提交").GetAwaiter().GetResult();
        var json = resp.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();

        if (json.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            if (errors.EnumerateArray().Any(IsStaleHead))
                return null;
            var text = errors.ToString();
            throw new InvalidOperationException($"创建提交失败：{(text.Length > 500 ? text[..500] : text)}");
        }
        return json.GetProperty("data").GetProperty("createCommitOnBranch").GetProperty("commit").GetProperty("oid").GetString();
    }

    /// <summary>分支头已经移动：GitHub 返回 STALE_DATA，或者消息里写着 “Expected branch to point to …”。</summary>
    private static bool IsStaleHead(JsonElement error) =>
        (error.TryGetProperty("type", out var t) && t.GetString() == "STALE_DATA")
        || (error.TryGetProperty("message", out var m) && m.GetString() is { } s
            && s.Contains("Expected branch to point to", StringComparison.OrdinalIgnoreCase));
}
