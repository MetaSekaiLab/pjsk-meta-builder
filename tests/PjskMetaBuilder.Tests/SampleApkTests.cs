using System.Text.Json;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Keys;
using PjskMetaBuilder.Core.Output;

namespace PjskMetaBuilder.Tests;

/// <summary>
/// 用真实安装包跑完整的 build。安装包很大、不进仓库：设置环境变量后才会执行，否则直接通过。
///   PJSK_SAMPLE_JP_APK = jp 7.1.0 原始包（pjsk-reverse-workflow Release build-41579208757-1）
///   PJSK_SAMPLE_CN_APK = cn 6.4.1 官网包（versionCode 4647）
/// 期望值来自 workflow identity.py 的输出（设计文档 §10）。
/// </summary>
public class SampleApkTests
{
    private static string? Sample(string env) =>
        Environment.GetEnvironmentVariable(env) is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static BuildResult BuildSample(string region, string apk)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pjsk-meta-builder-tests", Guid.NewGuid().ToString("N"));
        try
        {
            return MetaBuilder.Build(new BuildRequest(region, [apk], dir, Now: DateTimeOffset.UnixEpoch));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Jp710()
    {
        if (Sample("PJSK_SAMPLE_JP_APK") is not { } apk)
            return;
        var r = BuildSample("jp", apk);
        Assert.Equal("7.1.0", r.Meta.AppVersion);
        Assert.Equal("833770f2-7169-40b3-bb69-c0d27f312156", r.Meta.AppHash);
        Assert.Equal("7.1.0.10", r.Meta.DataVersion);
        Assert.Equal("2022.3.62f2", r.Meta.UnityVersion);
        Assert.Equal(300131, r.Meta.Package.VersionCode);
        Assert.Equal("5B30CBC1", KeyExtractor.Fingerprint(r.Keys.Api.Key));
        Assert.Equal("7C1CF10C", KeyExtractor.Fingerprint(r.Keys.Api.Iv));
        Assert.Null(r.Keys.Ab);
    }

    [Fact]
    public void Cn641()
    {
        if (Sample("PJSK_SAMPLE_CN_APK") is not { } apk)
            return;
        var r = BuildSample("cn", apk);
        Assert.Equal("6.4.1", r.Meta.AppVersion);
        Assert.Equal("d67a688b-ff78-4300-bfc5-2120c000d75d", r.Meta.AppHash);
        Assert.Equal("2022.3.62f3", r.Meta.UnityVersion);
        Assert.Equal(4647, r.Meta.Package.VersionCode);
        Assert.Equal("BA2B1D52", KeyExtractor.Fingerprint(r.Keys.Api.Key));
        Assert.Equal("3673BB77", KeyExtractor.Fingerprint(r.Keys.Api.Iv));
        Assert.NotNull(r.Keys.Ab);
        Assert.Equal("5B30CBC1", KeyExtractor.Fingerprint(r.Keys.Ab!.Key));

        // moe-info.json：数组两项，位串可还原为同一密钥
        var moeInfo = JsonSerializer.Deserialize<List<MoeInfoEntry>>(Artifacts.Serialize(Artifacts.MoeInfo(r.Keys)))!;
        Assert.Equal(2, moeInfo.Count);
        Assert.Equal(r.Keys.Api.Key, BitString.Decode(moeInfo[0].MoeKey));
        Assert.Equal(r.Keys.Ab.Iv, BitString.Decode(moeInfo[1].MoeSecret));
    }

    [Fact]
    public void RejectsWrongRegion()
    {
        if (Sample("PJSK_SAMPLE_CN_APK") is not { } apk)
            return;
        Assert.Throws<DataException>(() => BuildSample("jp", apk));
    }

    [Fact]
    public void ExpectedVersionCodeNewerThanPackageMeansNotReady()
    {
        if (Sample("PJSK_SAMPLE_CN_APK") is not { } apk)
            return;
        var dir = Path.Combine(Path.GetTempPath(), "pjsk-meta-builder-tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<SourceNotReadyException>(() =>
            MetaBuilder.Build(new BuildRequest("cn", [apk], dir, ExpectedVersionCode: 4648)));
    }
}
