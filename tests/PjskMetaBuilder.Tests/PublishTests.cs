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
        Directory.GetFiles(dir).ToDictionary(Path.GetFileName, File.ReadAllBytes)!;

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
