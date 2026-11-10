using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Publish;
using PjskMetaBuilder.Core.Sources;

namespace PjskMetaBuilder.Service;

/// <summary>执行一个任务：下载 → build → 发布。抛出的异常由队列按 <see cref="ErrorKinds"/> 分类。</summary>
public interface IJobRunner
{
    JobOutcome Run(BuildJobRequest request, CancellationToken ct);
}

/// <summary>
/// 真实流水线。每个任务一个临时目录（权限 700），安装包、解密后的 metadata、DummyDll 都在里面，结束后删除（§7.4）。
/// 服务里总是跑 Il2CppDumper：Nuverse 要生成结构文件，CP 用它反汇编 cctor 定位密钥（§5.5）。
/// </summary>
public sealed class PipelineRunner(ServiceOptions options, ILogger<PipelineRunner> logger) : IJobRunner
{
    public JobOutcome Run(BuildJobRequest request, CancellationToken ct)
    {
        if (options.Il2CppDumperDll is null)
            throw new SourceConfigurationException("服务没有配置 IL2CPPDUMPER_DLL");
        if (options.GitHubAppId is null || options.GitHubAppPrivateKey is null)
            throw new SourceConfigurationException("服务没有配置 GitHub App（GITHUB_APP_ID / GITHUB_APP_PRIVATE_KEY）");

        var region = Regions.Get(request.Region);
        var dir = Path.Combine(options.WorkRoot, $"{request.Region}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            void Log(string m) => logger.LogInformation("[{RequestId}] {Message}", request.RequestId, m);

            // 1. 下载
            var source = PackageSources.Create(request.Source, region, log: Log);
            var package = source.Fetch(region, Path.Combine(dir, "downloads"), request.ExpectedVersionCode);
            ct.ThrowIfCancellationRequested();

            // 2. build
            var outDir = Path.Combine(dir, "out");
            var build = MetaBuilder.Build(new BuildRequest(region.Code, package.Apks, outDir, package.Source,
                request.ExpectedVersionCode, options.Il2CppDumperDll, options.DotnetHost));
            foreach (var w in build.Warnings)
                logger.LogWarning("[{RequestId}] {Warning}", request.RequestId, w);
            Log($"build 完成：{build.Meta.AppVersion}（versionCode {build.Meta.Package.VersionCode}）");
            ct.ThrowIfCancellationRequested();

            // 3. 发布（token 只申请产物仓库一个）
            var parts = options.AppMetaRepo.Split('/');
            var token = new GitHubAppToken(options.GitHubAppId, options.GitHubAppPrivateKey)
                .CreateInstallationTokenAsync(parts[0], parts[1], ct).GetAwaiter().GetResult();
            var published = Publisher.Publish(new PublishRequest(region.Code, outDir, options.AppMetaRepo, Token: token));
            Log($"发布：{published.Result} {published.Path} {published.Commit}");

            return new JobOutcome(published.Result, build.Meta.AppVersion, build.Meta.Package.VersionCode,
                published.Commit, published.Path, published.Files);   // noop / rejected 时 path 为空：仓库里以 latest.json 为准
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "[{RequestId}] 删除任务目录失败", request.RequestId);
            }
        }
    }
}
