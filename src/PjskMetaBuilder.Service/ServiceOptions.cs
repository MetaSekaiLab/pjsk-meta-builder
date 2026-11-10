using System.Text;

namespace PjskMetaBuilder.Service;

/// <summary>服务配置，全部来自环境变量（§8）。密钥类只从环境变量或文件读，不进命令行参数。</summary>
public sealed record ServiceOptions
{
    /// <summary>MOE_HMAC_SECRET（或 MOE_HMAC_SECRET_FILE）：与同步服务共用，双向签名。</summary>
    public required byte[] HmacSecret { get; init; }
    public string ListenUrl { get; init; } = "http://0.0.0.0:8080";
    public string AppMetaRepo { get; init; } = "StarMoe-org/moesekai-app-meta";
    public string? GitHubAppId { get; init; }
    public string? GitHubAppPrivateKey { get; init; }
    public string? Il2CppDumperDll { get; init; }
    public string? DotnetHost { get; init; }
    /// <summary>每个任务的临时目录放在这里（权限 700，任务结束删除）。</summary>
    public string WorkRoot { get; init; } = Path.Combine(Path.GetTempPath(), "pjsk-meta-jobs");
    /// <summary>回调失败后的重试间隔（§7.2：10 秒、30 秒、2 分钟）。</summary>
    public IReadOnlyList<TimeSpan> CallbackRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    /// <summary>内存里保留的已结束任务数（GET /v1/builds/{id} 排查用）。</summary>
    public int KeepFinishedJobs { get; init; } = 200;

    public static ServiceOptions FromEnvironment(string? listenOverride = null)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        static string? Secret(string name) =>
            Env(name) ?? (Env(name + "_FILE") is { } f ? File.ReadAllText(f).Trim() : null);

        var hmac = Secret("MOE_HMAC_SECRET")
            ?? throw new ArgumentException("缺少 MOE_HMAC_SECRET（或 MOE_HMAC_SECRET_FILE）");
        if (hmac.Length < 32)
            throw new ArgumentException("MOE_HMAC_SECRET 至少 32 个字符");
        return new ServiceOptions
        {
            HmacSecret = Encoding.UTF8.GetBytes(hmac),
            ListenUrl = listenOverride ?? (Env("PORT") is { } port ? $"http://0.0.0.0:{port}" : "http://0.0.0.0:8080"),
            AppMetaRepo = Env("APP_META_REPO") ?? "StarMoe-org/moesekai-app-meta",
            GitHubAppId = Env("GITHUB_APP_ID"),
            GitHubAppPrivateKey = Secret("GITHUB_APP_PRIVATE_KEY"),
            Il2CppDumperDll = Env("IL2CPPDUMPER_DLL"),
            DotnetHost = Env("PJSK_DOTNET"),
            WorkRoot = Env("PJSK_WORK_ROOT") ?? Path.Combine(Path.GetTempPath(), "pjsk-meta-jobs"),
        };
    }
}
