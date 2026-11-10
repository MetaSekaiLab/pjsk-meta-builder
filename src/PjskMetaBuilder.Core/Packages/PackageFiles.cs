using System.IO.Compression;

namespace PjskMetaBuilder.Core.Packages;

/// <summary>
/// 把主包和拆分包当作一个虚拟文件系统：按路径查找，不合并 APK（因此不需要 APKEditor）。
/// 同一路径出现在多个包里时，取第一个打开的包（调用方按"主包在前"的顺序传入）。
/// </summary>
public sealed class PackageFiles : IDisposable
{
    private readonly List<(string Path, ZipArchive Zip)> _archives = [];
    private readonly Dictionary<string, (string Apk, ZipArchiveEntry Entry)> _index = new(StringComparer.Ordinal);

    public IReadOnlyList<string> ApkPaths => _archives.Select(a => a.Path).ToList();

    public static PackageFiles Open(IEnumerable<string> apkPaths)
    {
        var files = new PackageFiles();
        try
        {
            foreach (var path in apkPaths)
            {
                ZipArchive zip;
                try
                {
                    zip = ZipFile.OpenRead(path);
                }
                catch (InvalidDataException e)
                {
                    throw new DataException($"{Path.GetFileName(path)} 不是有效的 zip：{e.Message}");
                }
                files._archives.Add((path, zip));
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/'))
                        continue;
                    files._index.TryAdd(entry.FullName, (path, entry));
                }
            }
        }
        catch
        {
            files.Dispose();
            throw;
        }
        return files;
    }

    public bool Contains(string path) => _index.ContainsKey(path);

    public IEnumerable<string> Paths => _index.Keys;

    public long Size(string path) =>
        _index.TryGetValue(path, out var hit) ? hit.Entry.Length : throw new DataException($"安装包里找不到 {path}");

    public byte[] Read(string path)
    {
        if (!_index.TryGetValue(path, out var hit))
            throw new DataException($"安装包里找不到 {path}");
        using var stream = hit.Entry.Open();
        using var buffer = new MemoryStream(checked((int)hit.Entry.Length));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>每个 APK 自己的 AndroidManifest.xml（主包和拆分包都有）。</summary>
    public IEnumerable<(string Apk, byte[] Manifest)> Manifests()
    {
        foreach (var (path, zip) in _archives)
        {
            var entry = zip.GetEntry("AndroidManifest.xml");
            if (entry is null)
                continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return (path, buffer.ToArray());
        }
    }

    public void Dispose()
    {
        foreach (var (_, zip) in _archives)
            zip.Dispose();
        _archives.Clear();
        _index.Clear();
    }
}
