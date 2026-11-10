using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;
using PjskMetaBuilder.Core.Packages;

namespace PjskMetaBuilder.Core.Unity;

public sealed record BuildTag(string Raw, string ClientVersion, string DataVersion, string Build, string FoundIn);

/// <summary>
/// CP 服（jp）的 dataVersion 不在 production_android 里（那里是占位的 1.0.0.0），
/// 而在一个名为 JenkinsBuildManifest 的 MonoBehaviour 中：m_Name 之后的第一个字符串形如
/// <c>tags/7.1.0_7.1.0.10_1007</c>（客户端版本_dataVersion_构建号）。workflow 用正则扫同一字符串。
/// en 的形如 <c>release/6.1.0_6.1.0.0</c>（分支构建，没有构建号；en 的 production_android 同样是占位的 1.0.0.0）。
/// Nuverse 服没有这个对象。
/// </summary>
public static partial class BuildManifestReader
{
    public const string ObjectName = "JenkinsBuildManifest";
    private const string DataDir = "assets/bin/Data/";
    private const int MonoBehaviourClassId = 114;
    private const long MaxFileSize = 1 << 20; // 只看小的序列化文件

    // jp：tags/7.1.0_7.1.0.10_1007（标签构建）；en：release/6.1.0_6.1.0.0（分支构建，没有构建号）
    [GeneratedRegex(@"^(?:tags|release)/(\d+\.\d+\.\d+)_(\d+\.\d+\.\d+\.\d+)(?:_(\d+))?$")]
    private static partial Regex TagPattern();

    public static BuildTag? TryRead(PackageFiles files)
    {
        var found = new List<BuildTag>();
        foreach (var path in files.Paths)
        {
            if (!path.StartsWith(DataDir, StringComparison.Ordinal) || path[DataDir.Length..].Contains('/')
                || Path.HasExtension(path) || files.Size(path) > MaxFileSize)
                continue;
            var tag = TryReadFile(files.Read(path), path);
            if (tag is not null)
                found.Add(tag);
        }
        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new DataException($"找到多个 {ObjectName}：{string.Join(", ", found.Select(f => f.FoundIn))}"),
        };
    }

    private static BuildTag? TryReadFile(byte[] bytes, string path)
    {
        var manager = new AssetsManager();
        try
        {
            AssetsFileInstance inst;
            try
            {
                inst = manager.LoadAssetsFile(new MemoryStream(bytes), path, false);
            }
            catch (Exception)
            {
                return null; // 不是序列化文件
            }
            foreach (var info in inst.file.GetAssetsOfType(MonoBehaviourClassId))
            {
                byte[] raw;
                lock (inst.LockReader)
                {
                    inst.file.Reader.Position = info.GetAbsoluteByteOffset(inst.file);
                    raw = inst.file.Reader.ReadBytes((int)info.ByteSize);
                }
                string tag;
                try
                {
                    var r = new ObjectReader(raw);
                    r.ReadPPtr();
                    r.ReadByte();
                    r.Align4();
                    r.ReadPPtr();
                    if (r.ReadAlignedString() != ObjectName)
                        continue;
                    tag = r.ReadAlignedString();
                }
                catch (Exception e) when (e is DataException or ArgumentOutOfRangeException)
                {
                    continue; // 不是我们要的对象布局
                }
                var m = TagPattern().Match(tag);
                if (!m.Success)
                    throw new DataException($"{ObjectName} 的构建标签格式不认识：{tag}");
                return new BuildTag(tag, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, path);
            }
            return null;
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }
}
