using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PjskMetaBuilder.Core.Packages;

namespace PjskMetaBuilder.Core.Unity;

/// <summary>production_android（AndroidPlayerSettingConfig）里我们关心的字段。</summary>
public sealed record PlayerSettings(
    string Memo,
    string ClientMajorVersion,
    string ClientMinorVersion,
    string ClientBuildVersion,
    string Snapshot,
    string ClientVersionSuffix,
    string ClientDataMajorVersion,
    string ClientDataMinorVersion,
    string ClientDataBuildVersion,
    string ClientDataRevision,
    string CompanyName,
    string ProductName,
    string BundleIdentifier,
    string BundleVersion,
    string AssetHash,
    string ClientAppHash,
    string UnityVersion,
    string FoundIn)
{
    public string ClientVersion => $"{ClientMajorVersion}.{ClientMinorVersion}.{ClientBuildVersion}";
    public string DataVersion => $"{ClientDataMajorVersion}.{ClientDataMinorVersion}.{ClientDataBuildVersion}.{ClientDataRevision}";
    public string AbVersion => $"{ClientMajorVersion}.{ClientMinorVersion}.{ClientDataRevision}";
}

/// <summary>
/// 找到并读取 production_android。release 构建没有 typetree，按固定字段顺序读：
/// MonoBehaviour 基础头之后是 16 个字符串（与 workflow read_player_config、sssekai AndroidPlayerSettingConfig 一致）。
/// </summary>
public static class PlayerSettingsReader
{
    public const string ConfigName = "production_android";
    public const string ContainerPath = "playersettings/android/production_android";
    private const string DataDir = "assets/bin/Data/";
    private const int ResourceManagerClassId = 147;
    private const int MonoBehaviourClassId = 114;

    public static PlayerSettings Read(PackageFiles files)
    {
        if (files.Contains(DataDir + "globalgamemanagers"))
            return ReadViaResourceManager(files);
        if (files.Contains(DataDir + "data.unity3d"))
            return ReadFromBundle(files);
        throw new DataException("安装包里既没有 globalgamemanagers 也没有 data.unity3d");
    }

    /// <summary>CP 服：globalgamemanagers 的 ResourceManager → 容器路径 → 外部文件里的对象。</summary>
    private static PlayerSettings ReadViaResourceManager(PackageFiles files)
    {
        var manager = new AssetsManager();
        try
        {
            var ggm = LoadAssets(manager, files, "globalgamemanagers");
            var resourceManagers = ggm.file.GetAssetsOfType(ResourceManagerClassId);
            if (resourceManagers.Count != 1)
                throw new DataException($"globalgamemanagers 里应当正好有一个 ResourceManager，实际 {resourceManagers.Count} 个");

            var (fileId, pathId) = FindContainer(ObjectBytes(ggm, resourceManagers[0]), ContainerPath);
            if (fileId <= 0 || fileId > ggm.file.Metadata.Externals.Count)
                throw new DataException($"{ContainerPath} 指向的文件序号不合法：{fileId}");
            var external = ggm.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/');
            var targetName = external.Split('/')[^1];

            var target = LoadAssets(manager, files, targetName);
            var info = target.file.GetAssetInfo(pathId)
                ?? throw new DataException($"{targetName} 里没有 pathID {pathId}");
            var settings = ReadMonoBehaviour(ObjectBytes(target, info), target.file.Metadata.UnityVersion, DataDir + targetName);
            return settings;
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    /// <summary>Nuverse 服：data.unity3d 里所有序列化文件中，m_Name 为 production_android 的 MonoBehaviour 必须正好一个。</summary>
    private static PlayerSettings ReadFromBundle(PackageFiles files)
    {
        var manager = new AssetsManager();
        try
        {
            var bundleBytes = files.Read(DataDir + "data.unity3d");
            var bundle = manager.LoadBundleFile(new MemoryStream(bundleBytes), DataDir + "data.unity3d", true);
            var found = new List<PlayerSettings>();
            var names = bundle.file.GetAllFileNames();
            for (var i = 0; i < names.Count; i++)
            {
                if (!bundle.file.IsAssetsFile(i))
                    continue;
                var assets = manager.LoadAssetsFileFromBundle(bundle, i, false);
                foreach (var info in assets.file.GetAssetsOfType(MonoBehaviourClassId))
                {
                    var bytes = ObjectBytes(assets, info);
                    if (ReadMonoBehaviourName(bytes) != ConfigName)
                        continue;
                    found.Add(ReadMonoBehaviour(bytes, assets.file.Metadata.UnityVersion, $"{DataDir}data.unity3d/{names[i]}"));
                }
            }
            if (found.Count != 1)
                throw new DataException($"data.unity3d 里应当正好有一个 {ConfigName}，实际 {found.Count} 个");
            return found[0];
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static AssetsFileInstance LoadAssets(AssetsManager manager, PackageFiles files, string name)
    {
        var bytes = files.Read(DataDir + name);
        return manager.LoadAssetsFile(new MemoryStream(bytes), DataDir + name, false);
    }

    private static byte[] ObjectBytes(AssetsFileInstance inst, AssetFileInfo info)
    {
        lock (inst.LockReader)
        {
            var reader = inst.file.Reader;
            reader.Position = info.GetAbsoluteByteOffset(inst.file);
            return reader.ReadBytes((int)info.ByteSize);
        }
    }

    /// <summary>ResourceManager.m_Container：vector&lt;pair&lt;string, PPtr&lt;Object&gt;&gt;&gt;，是第一个字段。</summary>
    private static (int FileId, long PathId) FindContainer(byte[] bytes, string path)
    {
        var r = new ObjectReader(bytes);
        var count = r.ReadInt32();
        (int, long)? hit = null;
        for (var i = 0; i < count; i++)
        {
            var key = r.ReadAlignedString();
            var pptr = r.ReadPPtr();
            if (key == path)
            {
                if (hit is not null)
                    throw new DataException($"ResourceManager 里 {path} 出现了多次");
                hit = pptr;
            }
        }
        return hit ?? throw new DataException($"ResourceManager 里没有 {path}");
    }

    private static string ReadMonoBehaviourName(byte[] bytes)
    {
        try
        {
            var r = new ObjectReader(bytes);
            SkipMonoBehaviourHeader(ref r);
            return r.ReadAlignedString();
        }
        catch (Exception e) when (e is DataException or ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static void SkipMonoBehaviourHeader(ref ObjectReader r)
    {
        r.ReadPPtr();   // m_GameObject
        r.ReadByte();   // m_Enabled
        r.Align4();
        r.ReadPPtr();   // m_Script
    }

    private static PlayerSettings ReadMonoBehaviour(byte[] bytes, string unityVersion, string foundIn)
    {
        var r = new ObjectReader(bytes);
        SkipMonoBehaviourHeader(ref r);
        var name = r.ReadAlignedString();
        if (name != ConfigName)
            throw new DataException($"对象名是 {name}，不是 {ConfigName}");
        var f = new string[16];
        for (var i = 0; i < f.Length; i++)
            f[i] = r.ReadAlignedString();
        return new PlayerSettings(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9],
            f[10], f[11], f[12], f[13], f[14], f[15], unityVersion, foundIn);
    }
}
