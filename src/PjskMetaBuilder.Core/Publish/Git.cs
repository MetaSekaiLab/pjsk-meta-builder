using System.Diagnostics;

namespace PjskMetaBuilder.Core.Publish;

public sealed class GitException(string message, string stderr) : Exception(message)
{
    public string StdErr { get; } = stderr;
}

/// <summary>调用 git 命令行（只用于本地裸仓库和测试；GitHub 上的发布走 API）。</summary>
public sealed class Git(string? identityName = null, string? identityEmail = null, IReadOnlyDictionary<string, string>? env = null)
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
        // 提交身份固定，不受外部 GIT_AUTHOR_* / GIT_COMMITTER_* 环境变量影响
        foreach (var key in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            psi.Environment.Remove(key);
        if (identityName is not null && identityEmail is not null)
        {
            psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = identityName;
            psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = identityEmail;
        }
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
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

    /// <summary>形如 owner/name 且不是本地路径 → GitHub 仓库。</summary>
    public static bool IsGitHub(string repo) => !repo.Contains("://") && !Path.IsPathRooted(repo) && !Directory.Exists(repo)
        && repo.Count(c => c == '/') == 1;
}
