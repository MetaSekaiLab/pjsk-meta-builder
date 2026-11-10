using System.Diagnostics;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Publish;
using PjskMetaBuilder.Core.Sources;

namespace PjskMetaBuilder.Service;

public sealed record SelfTestResult(string Name, bool Ok, string Detail, long Millis);

/// <summary>
/// 出网与配置自检（部署后验证设计文档 §9 Q6）。每一项都很轻：查接口、对文件只取 1 个字节、读一次分支；
/// 不下载安装包，不往产物仓库写任何东西，输出里不含 token 和带签名的地址。
/// </summary>
public static class SelfTest
{
    public static IReadOnlyList<SelfTestResult> Run(ServiceOptions options)
    {
        var results = new List<SelfTestResult>
        {
            Check("Il2CppDumper", () =>
                options.Il2CppDumperDll is { } dll && File.Exists(dll) ? dll : throw new SourceConfigurationException("没有配置 IL2CPPDUMPER_DLL 或文件不存在")),
            Check("工作目录", () =>
            {
                Directory.CreateDirectory(options.WorkRoot);
                var probe = Path.Combine(options.WorkRoot, ".selftest-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, [1]);
                File.Delete(probe);
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.WorkRoot))!).AvailableFreeSpace;
                return $"{options.WorkRoot} 可写，剩余 {free >> 20} MB";
            }),
        };
        foreach (var code in new[] { "jp", "en", "hmt", "kr" })
        {
            results.Add(Check($"QooApp（{code}）", () =>
            {
                var p = new QooAppSource(QooAppCredentials.FromEnvironment()).Probe(Regions.Get(code));
                return $"{p.VersionName}（versionCode {p.VersionCode}），主包 {p.BaseHost}"
                    + (p.BaseSize is { } size ? $" {size >> 20} MB" : "") + (p.SplitHost is { } h ? $"，拆分包 {h}" : "");
            }));
        }
        results.Add(Check("cn 官网链接", () =>
        {
            var (final, total) = new OfficialUrlSource().Probe(Regions.Get("cn"));
            return $"{final.Host}，{total >> 20} MB";
        }));
        results.Add(Check("GitHub App", () =>
        {
            if (options.GitHubAppId is null || options.GitHubAppPrivateKey is null)
                throw new SourceConfigurationException("没有配置 GITHUB_APP_ID / GITHUB_APP_PRIVATE_KEY");
            var parts = options.AppMetaRepo.Split('/');
            var token = new GitHubAppToken(options.GitHubAppId, options.GitHubAppPrivateKey)
                .CreateInstallationTokenAsync(parts[0], parts[1]).GetAwaiter().GetResult();
            var repo = GitHubApiRepository.FromSlug(options.AppMetaRepo, "main", token);
            var head = repo.Head() ?? throw new DataException($"{options.AppMetaRepo} 没有 main 分支");
            var latest = repo.ReadFile(head, "jp/latest.json");
            return $"{options.AppMetaRepo} main@{head[..7]}，jp/latest.json {(latest is null ? "不存在" : $"{latest.Length} 字节")}";
        }));
        return results;
    }

    private static SelfTestResult Check(string name, Func<string> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return new SelfTestResult(name, true, action(), sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            return new SelfTestResult(name, false, $"[{ErrorKinds.Classify(e)}] {e.Message}", sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>最近一次自检结果，供 /healthz 显示（只给是否通过和时间，细节看日志）。</summary>
public sealed class SelfTestState
{
    public IReadOnlyList<SelfTestResult>? Results { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>PJSK_SELFTEST=1 时，服务启动后在后台跑一次自检并写日志。</summary>
internal sealed class StartupSelfTest(ServiceOptions options, SelfTestState state, ILogger<StartupSelfTest> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("开始出网自检");
        var results = await Task.Run(() => SelfTest.Run(options), stoppingToken);
        state.Results = results;
        state.At = DateTimeOffset.UtcNow;
        foreach (var r in results)
        {
            if (r.Ok)
                logger.LogInformation("自检 OK   {Name}（{Millis} ms）：{Detail}", r.Name, r.Millis, r.Detail);
            else
                logger.LogWarning("自检 FAIL {Name}（{Millis} ms）：{Detail}", r.Name, r.Millis, r.Detail);
        }
        logger.LogInformation("出网自检结束：{Ok}/{Total} 通过", results.Count(r => r.Ok), results.Count);
    }
}
