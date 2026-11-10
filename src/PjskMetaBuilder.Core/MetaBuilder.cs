using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PjskMetaBuilder.Core.Il2Cpp;
using PjskMetaBuilder.Core.Keys;
using PjskMetaBuilder.Core.Metadata;
using PjskMetaBuilder.Core.Output;
using PjskMetaBuilder.Core.Packages;
using PjskMetaBuilder.Core.Schema;
using PjskMetaBuilder.Core.Unity;

namespace PjskMetaBuilder.Core;

/// <summary>下载源上的版本比期望的旧：来源还没更新完，调用方稍后重试。</summary>
public sealed class SourceNotReadyException(string message) : DataException(message);

public sealed record BuildRequest(
    string Region,
    IReadOnlyList<string> Apks,
    string OutputDir,
    string Source = "local",
    long? ExpectedVersionCode = null,
    string? Il2CppDumperDll = null,
    string? DotnetHost = null,
    bool KeepWorkDir = false,
    DateTimeOffset? Now = null);

public sealed record BuildResult(
    AppMeta Meta,
    KeySet Keys,
    PlayerSettings Settings,
    ManifestInfo Manifest,
    IReadOnlyDictionary<string, string> WrittenFiles,
    IReadOnlyList<string> Warnings,
    string? DummyDllDir,
    SchemaResult? Schema = null);

/// <summary>设计文档 §3 的第 1–7 步（第一版 M1：不含结构文件）。不需要任何凭据。</summary>
public static partial class MetaBuilder
{
    public const string LibIl2Cpp = "lib/arm64-v8a/libil2cpp.so";
    public const string GlobalMetadata = "assets/bin/Data/Managed/Metadata/global-metadata.dat";

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    public static string BuilderVersion =>
        typeof(MetaBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static BuildResult Build(BuildRequest request)
    {
        var region = Regions.Get(request.Region);
        var warnings = new List<string>();
        using var files = PackageFiles.Open(request.Apks);

        // 1. 清单：主包是没有 split 属性的那个
        var manifests = files.Manifests().Select(m => (m.Apk, Info: AndroidManifest.Parse(m.Manifest))).ToList();
        var bases = manifests.Where(m => m.Info.Split is null).ToList();
        if (bases.Count != 1)
            throw new DataException($"应当正好有一个主包（没有 split 属性），实际 {bases.Count} 个");
        var (baseApk, manifest) = bases[0];
        if (manifest.Package != region.Package)
            throw new DataException($"安装包的包名是 {manifest.Package}，但 {region.Code} 应该是 {region.Package}");
        foreach (var split in manifests.Where(m => m.Info.Split is not null && m.Info.Package != region.Package))
            throw new DataException($"拆分包 {Path.GetFileName(split.Apk)} 的包名是 {split.Info.Package}");
        if (manifest.VersionCode is not { } versionCode || string.IsNullOrEmpty(manifest.VersionName))
            throw new DataException("清单缺少 versionName 或 versionCode");
        if (request.ExpectedVersionCode is { } expected && versionCode < expected)
            throw new SourceNotReadyException($"下载到的 versionCode {versionCode} 小于期望的 {expected}，来源还没更新完");

        // 2. 解密 metadata
        var metadata = MetadataDecryptor.Decrypt(files.Read(GlobalMetadata), files.Read(LibIl2Cpp));

        // 3. 身份
        var settings = PlayerSettingsReader.Read(files);
        if (!UuidPattern().IsMatch(settings.ClientAppHash))
            throw new DataException("clientAppHash 不是 UUID 格式");
        if (settings.BundleIdentifier != region.Package)
            warnings.Add($"production_android 的 bundleIdentifier 是 {settings.BundleIdentifier}，与包名 {region.Package} 不同");
        var appVersion = region.Nuverse ? manifest.VersionName : settings.ClientVersion;
        if (!Util.AppVersion.IsValid(appVersion))
            throw new DataException($"appVersion 格式不对：{appVersion}");

        // dataVersion：jp 的 production_android 里是占位值，真实值在 JenkinsBuildManifest 的构建标签里
        var dataVersion = settings.DataVersion;
        var tag = region.Nuverse ? null : BuildManifestReader.TryRead(files);
        if (tag is not null)
        {
            if (tag.ClientVersion != settings.ClientVersion)
                throw new DataException($"构建标签的版本 {tag.ClientVersion} 与 production_android 的 {settings.ClientVersion} 不一致");
            dataVersion = tag.DataVersion;
        }
        else if (!region.Nuverse)
            warnings.Add($"没有找到 {BuildManifestReader.ObjectName}，dataVersion 取 production_android 的 {dataVersion}");
        if (!Util.AppVersion.IsValid(dataVersion))
            throw new DataException($"dataVersion 格式不对：{dataVersion}");
        if (!region.Nuverse && settings.ClientVersion != manifest.VersionName)
            warnings.Add($"Unity 配置的版本 {settings.ClientVersion} 与 versionName {manifest.VersionName} 不同");

        // 4. Il2CppDumper（Nuverse 服必需：生成结构文件；CP 服给了也会跑，用于反汇编解析密钥）
        if (region.Nuverse && request.Il2CppDumperDll is null)
            throw new ArgumentException($"{region.Code} 是 Nuverse 服，需要 Il2CppDumper 生成结构文件（--il2cppdumper）");
        string? dummyDir = null, soPath = null;
        var work = Path.Combine(Path.GetTempPath(), "pjsk-meta-builder", Guid.NewGuid().ToString("N"));
        try
        {
            if (request.Il2CppDumperDll is not null)
            {
                Directory.CreateDirectory(work);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                soPath = Path.Combine(work, "libil2cpp.so");
                var meta = Path.Combine(work, "global-metadata.dat");
                File.WriteAllBytes(soPath, files.Read(LibIl2Cpp));
                File.WriteAllBytes(meta, metadata);
                dummyDir = new Il2CppDumperRunner(request.Il2CppDumperDll, request.DotnetHost)
                    .Run(soPath, meta, Path.Combine(work, "dump"));
            }

            // 5. 密钥：有 dump 输出就反汇编 cctor 动态定位（能跟上密钥更换），否则用已知哈希表
            var keys = ResolveKeys(metadata, region, dummyDir, soPath, warnings);

            // 6. 写产物
            Directory.CreateDirectory(request.OutputDir);
            var written = new Dictionary<string, string>();
            var moeInfo = Artifacts.Serialize(Artifacts.MoeInfo(keys));
            WriteFile(request.OutputDir, "moe-info.json", moeInfo, written);
            SchemaResult? schema = null;
            if (region.Nuverse)
            {
                schema = SchemaGenerator.Generate(dummyDir!, appVersion);
                WriteFile(request.OutputDir, "schema.json", SchemaGenerator.Serialize(schema.Schema), written);
                warnings.AddRange(schema.Warnings.Select(w => "结构文件：" + w));
            }

            var meta2 = new AppMeta(
                Artifacts.MetaSchema,
                region.Code,
                appVersion,
                settings.ClientAppHash,
                dataVersion,
                settings.UnityVersion,
                settings.ClientVersion,
                settings.AbVersion,
                new PackageMeta(manifest.Package, manifest.VersionName, versionCode, Sha256File(baseApk), request.Source),
                new SortedDictionary<string, string>(written, StringComparer.Ordinal),
                (request.Now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                new BuilderMeta(BuilderVersion, Il2CppDumperRunner.PinnedRevision));
            WriteFile(request.OutputDir, "meta.json", Artifacts.Serialize(meta2), written);

            return new BuildResult(meta2, keys, settings, manifest, written, warnings,
                request.KeepWorkDir ? dummyDir : null, schema);
        }
        finally
        {
            if (!request.KeepWorkDir && Directory.Exists(work))
                Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// 有 Il2CppDumper 输出（so + script.json）就反汇编 APIManager..cctor 动态定位密钥字段（§9 Q1 方案 1），
    /// 这样密钥更换也能跟上；同时与已知哈希表交叉校验并记录。解析失败或没有 dump 输出时回退到已知哈希表。
    /// </summary>
    private static Keys.KeySet ResolveKeys(byte[] metadata, RegionInfo region, string? dummyDir, string? soPath, List<string> warnings)
    {
        if (dummyDir is null || soPath is null)
            return KeyExtractor.Extract(metadata, region);

        var scriptJson = Path.Combine(Path.GetDirectoryName(dummyDir)!, "script.json");
        try
        {
            var resolved = KeyResolver.Resolve(soPath, scriptJson, metadata, region.Nuverse);
            var known = KeyExtractor.KnownHashes(region);
            if (!resolved.Hashes.SequenceEqual(known, StringComparer.OrdinalIgnoreCase))
                warnings.Add($"密钥字段与已知哈希表不同（可能已更换），以 cctor 反汇编结果为准："
                    + string.Join("、", resolved.Hashes.Select(h => h[..8] + "…")));
            return resolved.Keys;
        }
        catch (DataException e)
        {
            warnings.Add($"cctor 反汇编解析密钥失败，回退到已知哈希表：{e.Message}");
            return KeyExtractor.Extract(metadata, region);
        }
    }

    private static void WriteFile(string dir, string name, byte[] bytes, Dictionary<string, string> written)
    {
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        written[name] = Artifacts.Sha256Hex(bytes);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
