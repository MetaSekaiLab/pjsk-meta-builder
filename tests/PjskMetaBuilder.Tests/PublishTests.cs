using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Output;
using PjskMetaBuilder.Core.Publish;

namespace PjskMetaBuilder.Tests;

public class UpdatePlannerTests
{
    private static VersionIdentity V(string version, string hash, long code) => new(version, hash, code);

    [Fact]
    public void FirstPublishAdvances()
    {
        var p = UpdatePlanner.Plan(null, V("6.4.1", "a", 10));
        Assert.Equal((PublishOutcome.Advanced, "6.4.1", true), (p.Outcome, p.Directory, p.UpdateLatest));
    }

    [Fact]
    public void NewerVersionAdvances() =>
        Assert.Equal(PublishOutcome.Advanced, UpdatePlanner.Plan(V("6.9.1", "a", 10), V("6.10.0", "b", 11)).Outcome);

    [Fact]
    public void OlderVersionOnlySnapshots()
    {
        var p = UpdatePlanner.Plan(V("6.4.1", "a", 10), V("6.4.0", "b", 9));
        Assert.Equal((PublishOutcome.SnapshotOnly, "6.4.0", false), (p.Outcome, p.Directory, p.UpdateLatest));
    }

    [Fact]
    public void SameBuildIsNoop() =>
        Assert.Equal(PublishOutcome.Noop, UpdatePlanner.Plan(V("6.4.1", "a", 10), V("6.4.1", "A", 10)).Outcome);

    [Fact]
    public void SameVersionNewerVersionCodeAdvancesToPlusDirectory()
    {
        var p = UpdatePlanner.Plan(V("6.4.1", "a", 10), V("6.4.1", "b", 12));
        Assert.Equal((PublishOutcome.Advanced, "6.4.1+12", true), (p.Outcome, p.Directory, p.UpdateLatest));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public void SameVersionNotNewerVersionCodeIsRejected(long code) =>
        Assert.Equal(PublishOutcome.Rejected, UpdatePlanner.Plan(V("6.4.1", "a", 10), V("6.4.1", "b", code)).Outcome);
}

/// <summary>构造假的 build 输出目录（随机密钥，不含真实数据）。</summary>
internal static class FakeBuild
{
    public static string Create(string region, string appVersion, string appHash, long versionCode, bool nuverse = false,
        Action<Dictionary<string, byte[]>>? tamper = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmb-fake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, byte[]>();
        var entries = Enumerable.Range(0, nuverse ? 2 : 1)
            .Select(_ => new MoeInfoEntry(BitString.Encode(RandomNumberGenerator.GetBytes(16)), BitString.Encode(RandomNumberGenerator.GetBytes(16))))
            .ToList();
        files["moe-info.json"] = Artifacts.Serialize(entries);
        if (nuverse)
            files["schema.json"] = Encoding.UTF8.GetBytes("""{"classes":{},"schema":1,"source":{"appVersion":"x"},"tables":{}}""");
        var meta = new AppMeta(1, region, appVersion, appHash, appVersion + ".1", "2022.3.62f3", appVersion, appVersion,
            new PackageMeta(Regions.Get(region).Package, appVersion, versionCode, new string('0', 64), "local"),
            new SortedDictionary<string, string>(files.ToDictionary(kv => kv.Key, kv => Artifacts.Sha256Hex(kv.Value)), StringComparer.Ordinal),
            "2026-11-10T00:00:00Z", new BuilderMeta("0.1.0", "4741d46"));
        files["meta.json"] = Artifacts.Serialize(meta);
        tamper?.Invoke(files);
        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return dir;
    }
}

public class ContentLinterTests
{
    private static Dictionary<string, byte[]> Load(string dir) =>
        Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    private const string Hash = "d67a688b-ff78-4300-bfc5-2120c000d75d";

    [Fact]
    public void ValidDirectoryPasses()
    {
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("jp", "7.1.0", Hash, 1)), nuverse: false);
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("cn", "6.4.1", Hash, 1, nuverse: true)), nuverse: true);
    }

    [Fact]
    public void RejectsExtraFile() => Assert.Throws<DataException>(() =>
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("jp", "7.1.0", Hash, 1, tamper: f => f["notes.txt"] = [1])), false));

    [Fact]
    public void RejectsBadBitString() => Assert.Throws<DataException>(() =>
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("jp", "7.1.0", Hash, 1,
            tamper: f => f["moe-info.json"] = Encoding.UTF8.GetBytes("""[{"Moe-Key":"0101","Moe-Secret":"1"}]"""))), false));

    [Fact]
    public void RejectsShaMismatch() => Assert.Throws<DataException>(() =>
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("cn", "6.4.1", Hash, 1, nuverse: true,
            tamper: f => f["schema.json"] = Encoding.UTF8.GetBytes("""{"classes":{},"schema":1,"source":{},"tables":{}}"""))), true));

    [Fact]
    public void RejectsLongHexInSchema() => Assert.Throws<DataException>(() =>
    {
        var dir = FakeBuild.Create("cn", "6.4.1", Hash, 1, nuverse: true);
        var files = Load(dir);
        files["schema.json"] = Encoding.UTF8.GetBytes("{\"classes\":{},\"schema\":1,\"source\":{\"x\":\"" + new string('a', 40) + "\"},\"tables\":{}}");
        ContentLinter.CheckDirectory(files, true);
    });

    [Fact]
    public void RejectsMissingSchemaForNuverse() => Assert.Throws<DataException>(() =>
        ContentLinter.CheckDirectory(Load(FakeBuild.Create("cn", "6.4.1", Hash, 1)), true));
}

public class PublisherTests
{
    private const string HashA = "11111111-1111-4111-8111-111111111111";
    private const string HashB = "22222222-2222-4222-8222-222222222222";

    private static string NewBareRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), "pmb-bare", Guid.NewGuid().ToString("N") + ".git");
        new Git().Check(null, "init", "-q", "--bare", "-b", "main", path);
        return path;
    }

    private static PublishResult Publish(string repo, string region, string dir, bool dryRun = false) =>
        Publisher.Publish(new PublishRequest(region, dir, repo, DryRun: dryRun));

    private static string[] Tree(string repo) =>
        new Git().Check(null, "--git-dir", repo, "ls-tree", "-r", "--name-only", "main")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void FullLifecycle()
    {
        var repo = NewBareRepo();

        var first = Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashA, 100));
        Assert.Equal(("advanced", "jp/7.1.0"), (first.Result, first.Path));
        Assert.NotNull(first.Commit);

        Assert.Equal("noop", Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashA, 100)).Result);

        var older = Publish(repo, "jp", FakeBuild.Create("jp", "7.0.0", HashB, 90));
        Assert.Equal(("snapshot_only", "jp/7.0.0"), (older.Result, older.Path));

        var rebuild = Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashB, 101));
        Assert.Equal(("advanced", "jp/7.1.0+101"), (rebuild.Result, rebuild.Path));

        Assert.Equal("rejected", Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashA, 99)).Result);

        Assert.Equal(["jp/7.0.0/meta.json", "jp/7.0.0/moe-info.json", "jp/7.1.0+101/meta.json", "jp/7.1.0+101/moe-info.json",
            "jp/7.1.0/meta.json", "jp/7.1.0/moe-info.json", "jp/latest.json"], Tree(repo).Order(StringComparer.Ordinal).ToArray());

        Assert.Equal($"{PublishRequest.BotName} <{PublishRequest.BotEmail}>|{PublishRequest.BotName} <{PublishRequest.BotEmail}>",
            new Git().Check(null, "--git-dir", repo, "log", "-1", "--format=%an <%ae>|%cn <%ce>", "main").Trim());

        var latest = JsonDocument.Parse(new Git().Check(null, "--git-dir", repo, "show", "main:jp/latest.json")).RootElement;
        Assert.Equal(101, latest.GetProperty("package").GetProperty("versionCode").GetInt64());
    }

    [Fact]
    public void DryRunDoesNotPush()
    {
        var repo = NewBareRepo();
        var r = Publish(repo, "cn", FakeBuild.Create("cn", "6.4.1", HashA, 4647, nuverse: true), dryRun: true);
        Assert.Equal(("advanced", true, null), (r.Result, r.DryRun, r.Commit));
        var (code, _, _) = new Git().Run(null, "--git-dir", repo, "rev-parse", "--verify", "main");
        Assert.NotEqual(0, code); // 仍然是空仓库
    }

    [Fact]
    public void ExistingDirectoryOfAnotherBuildIsAnError()
    {
        var repo = NewBareRepo();
        Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashA, 100));
        Publish(repo, "jp", FakeBuild.Create("jp", "7.2.0", HashA, 110));
        // 7.1.0 目录已存在且属于 HashA；用另一个构建补 7.1.0 快照必须报错
        Assert.Throws<DataException>(() => Publish(repo, "jp", FakeBuild.Create("jp", "7.1.0", HashB, 100)));
    }

    /// <summary>第一次提交前别人抢先推了一个提交：应当重新读取、重新判定，并在新的分支头上提交成功。</summary>
    [Fact]
    public void RetriesWhenBranchMovesBeforeCommit()
    {
        var path = NewBareRepo();
        var inner = new LocalGitRepository(path, "main", PublishRequest.BotName, PublishRequest.BotEmail);
        Publish(path, "cn", FakeBuild.Create("cn", "6.4.1", HashA, 4647, nuverse: true));
        var racing = new RacingRepository(inner, () =>
            Publish(path, "jp", FakeBuild.Create("jp", "7.1.0", HashA, 100)));

        var r = Publisher.Publish(new PublishRequest("cn", FakeBuild.Create("cn", "6.5.0", HashB, 4700, nuverse: true), path,
            RetryBaseDelay: TimeSpan.Zero), racing);

        Assert.Equal(("advanced", "cn/6.5.0", "6.4.1"), (r.Result, r.Path, r.OldAppVersion));
        Assert.Equal(2, racing.Commits);   // 第一次因分支已移动失败，第二次成功
        Assert.Contains("jp/latest.json", Tree(path));
        Assert.Contains("cn/6.5.0/schema.json", Tree(path));
    }

    /// <summary>在第一次 Commit 之前先执行一次 <paramref name="race"/>（模拟并发提交）。</summary>
    private sealed class RacingRepository(IMetaRepository inner, Action race) : IMetaRepository
    {
        public int Commits { get; private set; }
        public string? Head() => inner.Head();
        public byte[]? ReadFile(string commit, string path) => inner.ReadFile(commit, path);
        public string? Commit(string? parent, IReadOnlyDictionary<string, byte[]> files, string message)
        {
            if (Commits++ == 0)
                race();
            return inner.Commit(parent, files, message);
        }
    }

    [Fact]
    public void RegionMismatchIsRejected()
    {
        var repo = NewBareRepo();
        Assert.Throws<DataException>(() => Publish(repo, "en", FakeBuild.Create("jp", "7.1.0", HashA, 100)));
    }
}

public class GitHubAppTokenTests
{
    [Fact]
    public void JwtIsSignedWithAppKeyAndCarriesClaims()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = new GitHubAppToken("12345", pem).CreateJwt(now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
        var payload = JsonDocument.Parse(B64(parts[1])).RootElement;
        Assert.Equal("12345", payload.GetProperty("iss").GetString());
        Assert.Equal(now.ToUnixTimeSeconds() - 60, payload.GetProperty("iat").GetInt64());
        Assert.True(payload.GetProperty("exp").GetInt64() - now.ToUnixTimeSeconds() <= 600);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

public class GitHubApiRepositoryTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            Requests.Add((request, body));
            return respond(request, body);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));
    }

    private static HttpResponseMessage Json(string json, System.Net.HttpStatusCode code = System.Net.HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GitHubApiRepository Repo, FakeHandler Handler) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new GitHubApiRepository("o", "r", "main", "tkn", new HttpClient(handler), "https://api.test"), handler);
    }

    [Fact]
    public void HeadOfEmptyRepositoryIsNull()
    {
        var (repo, _) = Create((_, _) => Json("""{"message":"Git Repository is empty."}""", System.Net.HttpStatusCode.Conflict));
        Assert.Null(repo.Head());
    }

    [Fact]
    public void HeadReadsRefSha()
    {
        var (repo, h) = Create((_, _) => Json("""{"object":{"sha":"abc"}}"""));
        Assert.Equal("abc", repo.Head());
        Assert.Equal("https://api.test/repos/o/r/git/ref/heads/main", h.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer tkn", h.Requests[0].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void ReadFileEscapesPathAndPinsCommit()
    {
        var (repo, h) = Create((req, _) => req.RequestUri!.AbsolutePath.Contains("missing")
            ? Json("""{"message":"Not Found"}""", System.Net.HttpStatusCode.NotFound)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        Assert.Equal(new byte[] { 1, 2, 3 }, repo.ReadFile("c0ffee", "jp/7.1.0+101/meta.json"));
        Assert.Null(repo.ReadFile("c0ffee", "jp/missing/meta.json"));
        var uri = h.Requests[0].Request.RequestUri!;
        Assert.Equal("/repos/o/r/contents/jp/7.1.0%2B101/meta.json", uri.AbsolutePath);
        Assert.Equal("?ref=c0ffee", uri.Query);
        Assert.Contains("application/vnd.github.raw+json", h.Requests[0].Request.Headers.Accept.ToString());
    }

    [Fact]
    public void CommitSendsSingleMutationWithExpectedHead()
    {
        var (repo, h) = Create((_, _) => Json("""{"data":{"createCommitOnBranch":{"commit":{"oid":"new"}}}}"""));
        var oid = repo.Commit("old", new Dictionary<string, byte[]> { ["cn/latest.json"] = "{}"u8.ToArray(), ["cn/6.4.1/meta.json"] = [0xff] }, "cn: ∅ → 6.4.1");

        Assert.Equal("new", oid);
        Assert.Single(h.Requests);
        Assert.Equal("https://api.test/graphql", h.Requests[0].Request.RequestUri!.ToString());
        var input = JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetProperty("variables").GetProperty("input");
        Assert.Equal("old", input.GetProperty("expectedHeadOid").GetString());
        Assert.Equal("o/r", input.GetProperty("branch").GetProperty("repositoryNameWithOwner").GetString());
        Assert.Equal("cn: ∅ → 6.4.1", input.GetProperty("message").GetProperty("headline").GetString());
        var additions = input.GetProperty("fileChanges").GetProperty("additions").EnumerateArray()
            .Select(a => (a.GetProperty("path").GetString(), a.GetProperty("contents").GetString())).ToArray();
        Assert.Equal([("cn/6.4.1/meta.json", "/w=="), ("cn/latest.json", "e30=")], additions);
    }

    [Fact]
    public void StaleHeadReturnsNull()
    {
        var (repo, _) = Create((_, _) => Json("""{"data":{"createCommitOnBranch":null},"errors":[{"type":"STALE_DATA","message":"Expected branch to point to \"old\" but it did not. Pull and try again."}]}"""));
        Assert.Null(repo.Commit("old", new Dictionary<string, byte[]> { ["a"] = [1] }, "m"));
    }

    [Fact]
    public void OtherGraphQlErrorsThrow()
    {
        var (repo, _) = Create((_, _) => Json("""{"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by integration"}]}"""));
        Assert.Throws<InvalidOperationException>(() => repo.Commit("old", new Dictionary<string, byte[]> { ["a"] = [1] }, "m"));
    }

    [Fact]
    public void ServerErrorsAreRetryable()
    {
        var (repo, _) = Create((_, _) => Json("{}", System.Net.HttpStatusCode.BadGateway));
        Assert.Throws<HttpRequestException>(() => repo.Head());
    }

    [Fact]
    public void CannotCreateFirstCommitOnEmptyRepository()
    {
        var (repo, h) = Create((_, _) => Json("{}"));
        Assert.Throws<InvalidOperationException>(() => repo.Commit(null, new Dictionary<string, byte[]> { ["a"] = [1] }, "m"));
        Assert.Empty(h.Requests);
    }
}

public class PemNormalizationTests
{
    private static readonly string Pem = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();

    [Fact]
    public void AcceptsRawEscapedAndBase64Pem()
    {
        Assert.Equal(Pem.Trim(), GitHubAppToken.NormalizePem(Pem));
        Assert.Equal(Pem.Trim(), GitHubAppToken.NormalizePem(Pem.Trim().Replace("\n", "\\n")));   // 面板里把换行写成 \n
        Assert.Equal(Pem, GitHubAppToken.NormalizePem(Convert.ToBase64String(Encoding.UTF8.GetBytes(Pem))));
        // 三种写法都能签出可验证的 JWT
        foreach (var form in new[] { Pem, Pem.Trim().Replace("\n", "\\n"), Convert.ToBase64String(Encoding.UTF8.GetBytes(Pem)) })
            Assert.Equal(3, new GitHubAppToken("1", form).CreateJwt().Split('.').Length);
    }

    [Fact]
    public void RejectsGarbage() =>
        Assert.Throws<ArgumentException>(() => GitHubAppToken.NormalizePem("not a key"));
}
