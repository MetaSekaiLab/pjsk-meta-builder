using System.Diagnostics;
using System.Text;

namespace PjskMetaBuilder.Core.Publish;

public sealed class GitException(string message, string stderr) : Exception(message)
{
    public string StdErr { get; } = stderr;
}

/// <summary>
/// 调用 git 命令行。token 通过 GIT_CONFIG_* 环境变量注入 http.extraheader：不进命令行参数，也不写进 .git/config。
/// </summary>
public sealed class Git(string? token = null, string? identityName = null, string? identityEmail = null)
{
    public (int Code, string Out, string Err) Run(string? cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (cwd is not null)
            psi.WorkingDirectory = cwd;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        // 提交身份固定为 bot，不受外部 GIT_AUTHOR_* / GIT_COMMITTER_* 环境变量影响
        foreach (var key in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            psi.Environment.Remove(key);
        if (identityName is not null && identityEmail is not null)
        {
            psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = identityName;
            psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = identityEmail;
        }
        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}";
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 git");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public string Check(string? cwd, params string[] args)
    {
        var (code, output, err) = Run(cwd, args);
        if (code != 0)
            throw new GitException($"git {args[0]} 失败：{Trim(err)}", err);
        return output;
    }

    public static string Trim(string s) => s.Length > 500 ? s[..500] : s.Trim();

    /// <summary>owner/name → https 地址；已经是 URL 或本地路径（测试用裸仓库）就原样返回。</summary>
    public static string RemoteUrl(string repo) =>
        repo.Contains("://") || Path.IsPathRooted(repo) || Directory.Exists(repo) ? repo : $"https://github.com/{repo}.git";

    public static bool IsGitHub(string repo) => !repo.Contains("://") && !Path.IsPathRooted(repo) && !Directory.Exists(repo)
        && repo.Count(c => c == '/') == 1;
}
