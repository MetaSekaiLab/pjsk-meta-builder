using System.Text;

namespace PjskMetaBuilder.Core.Publish;

/// <summary>
/// 本地裸仓库（测试和离线演练用）。只用底层命令：临时索引 → write-tree → commit-tree → update-ref（带旧值，等价于乐观并发）。
/// 文件按 UTF-8 文本读出；产物都是 JSON，足够用。
/// </summary>
public sealed class LocalGitRepository(string gitDir, string branch, string committerName, string committerEmail) : IMetaRepository
{
    private const string ZeroOid = "0000000000000000000000000000000000000000";
    private readonly Git _git = new(committerName, committerEmail);
    private string Ref => $"refs/heads/{branch}";

    public string? Head()
    {
        var (code, output, _) = _git.Run(null, "--git-dir", gitDir, "rev-parse", "--verify", "-q", Ref);
        return code == 0 ? output.Trim() : null;
    }

    public byte[]? ReadFile(string commit, string path)
    {
        var (code, output, _) = _git.Run(null, "--git-dir", gitDir, "show", $"{commit}:{path}");
        return code == 0 ? Encoding.UTF8.GetBytes(output) : null;
    }

    public string? Commit(string? parent, IReadOnlyDictionary<string, byte[]> files, string message)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pjsk-meta-index", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var git = new Git(committerName, committerEmail, new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(tmp, "index") });
            if (parent is null)
                git.Check(null, "--git-dir", gitDir, "read-tree", "--empty");
            else
                git.Check(null, "--git-dir", gitDir, "read-tree", parent);
            var i = 0;
            foreach (var (path, bytes) in files)
            {
                var blobFile = Path.Combine(tmp, $"blob{i++}");
                File.WriteAllBytes(blobFile, bytes);
                var blob = git.Check(null, "--git-dir", gitDir, "hash-object", "-w", blobFile).Trim();
                git.Check(null, "--git-dir", gitDir, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
            }
            var tree = git.Check(null, "--git-dir", gitDir, "write-tree").Trim();
            string[] commitArgs = parent is null
                ? ["--git-dir", gitDir, "commit-tree", tree, "-m", message]
                : ["--git-dir", gitDir, "commit-tree", tree, "-p", parent, "-m", message];
            var commit = git.Check(null, commitArgs).Trim();
            var (code, _, _) = git.Run(null, "--git-dir", gitDir, "update-ref", Ref, commit, parent ?? ZeroOid);
            return code == 0 ? commit : null;
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
