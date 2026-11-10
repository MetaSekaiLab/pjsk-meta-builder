namespace PjskMetaBuilder.Core.Publish;

/// <summary>
/// 产物仓库的最小读写接口。Publisher 只依赖它：读某个提交上的文件，以乐观并发的方式在分支头上追加一个提交。
/// GitHub 上用 <see cref="GitHubApiRepository"/>（API 提交，由 GitHub 签名）；本地裸仓库用 <see cref="LocalGitRepository"/>（测试用）。
/// </summary>
public interface IMetaRepository
{
    /// <summary>分支当前指向的提交；分支不存在（空仓库）时返回 null。</summary>
    string? Head();

    /// <summary>读取 <paramref name="commit"/> 上的文件；不存在返回 null。</summary>
    byte[]? ReadFile(string commit, string path);

    /// <summary>
    /// 以 <paramref name="parent"/> 为父提交写入 <paramref name="files"/>（新增或覆盖，不删除），生成一个提交并移动分支。
    /// 分支头已经不是 <paramref name="parent"/>（别人先提交了）时不做任何改动，返回 null。
    /// </summary>
    string? Commit(string? parent, IReadOnlyDictionary<string, byte[]> files, string message);
}

public static class MetaRepositories
{
    /// <summary>owner/name → GitHub API；其他（本地路径）→ 本地裸仓库。</summary>
    public static IMetaRepository Open(PublishRequest request) =>
        Git.IsGitHub(request.Repo)
            ? GitHubApiRepository.FromSlug(request.Repo, request.Branch, request.Token)
            : new LocalGitRepository(request.Repo, request.Branch, request.CommitterName, request.CommitterEmail);
}
