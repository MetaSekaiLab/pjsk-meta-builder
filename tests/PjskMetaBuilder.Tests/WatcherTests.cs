using Microsoft.Extensions.Logging.Abstractions;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Sources;
using PjskMetaBuilder.Service;

namespace PjskMetaBuilder.Tests;

public class VersionProbeParsingTests
{
    [Theory]
    [InlineData("pjsk_51199071a_v23509_4647_277c_1792657704.apk", 4647L)]
    [InlineData("pjsk_x_v24000_4800_aa_1.apk", 4800L)]
    [InlineData("apk.apk", null)]
    [InlineData("pjsk_v23509.apk", null)]
    public void OfficialFileNameVersionCode(string name, long? expected) =>
        Assert.Equal(expected, OfficialUrlSource.VersionCodeFromFileName(name));

    [Fact]
    public void GooglePlayDs5()
    {
        // 按真实页面 ds:5 的结构造的最小样本：[1][2][140][0][0][0] = versionName，[1][2][145][0][1][0] = 更新时间
        var app = new object?[146];
        app[140] = new object[] { new object[] { new object[] { "7.2.0" } } };
        app[145] = new object[] { new object[] { "Nov 11, 2026", new object[] { 1792054556L, 0 } } };
        var data = System.Text.Json.JsonSerializer.Serialize(new object?[] { null, new object?[] { null, null, app } });
        var html = $"<script>AF_initDataCallback({{key: 'ds:4', hash: '1', data:[1], sideChannel: {{}}}});</script>" +
                   $"<script>AF_initDataCallback({{key: 'ds:5', hash: '12', data:{data}, sideChannel: {{}}}});</script>";
        var info = GooglePlayPage.Parse(html);
        Assert.Equal("7.2.0", info.VersionName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792054556), info.UpdatedAt);

        Assert.Throws<DataException>(() => GooglePlayPage.Parse("<html>没有数据</html>"));
        Assert.Throws<DataException>(() => GooglePlayPage.Parse(html.Replace("7.2.0", "Varies with device")));
    }
}

internal sealed class FakeProbes : IVersionProbes
{
    public Dictionary<string, LatestVersion?> Latests { get; } = new()
    {
        ["jp"] = new("7.1.0", 300131), ["en"] = new("6.1.0", 100), ["hmt"] = new("6.4.0", 200),
        ["kr"] = new("6.4.0", 300), ["cn"] = new("6.4.1", 4647),
    };
    public long? Official { get; set; } = 4647;
    public Dictionary<string, QooAppVersion> Qoo { get; } = new()
    {
        ["jp"] = new("7.1.0", 300131, true), ["en"] = new("6.1.0", 100, true),
        ["hmt"] = new("6.4.0", 200, true), ["kr"] = new("6.4.0", 300, true),
    };
    public Dictionary<string, string> Play { get; } = new() { ["jp"] = "7.1.0", ["en"] = "6.1.0", ["hmt"] = "6.4.0", ["kr"] = "6.4.0" };
    public Exception? QooError { get; set; }
    public int QooCalls;

    public LatestVersion? Latest(string region) => Latests[region];
    public long? OfficialVersionCode(RegionInfo region) => Official;
    public QooAppVersion QooApp(RegionInfo region)
    {
        QooCalls++;
        return QooError is { } e ? throw e : Qoo[region.Code];
    }
    public GooglePlayInfo GooglePlay(RegionInfo region) => new(Play[region.Code], null);
}

internal sealed class FakeSink : IJobSink
{
    public List<BuildJobRequest> Requests { get; } = [];
    public bool Busy { get; set; }

    public (EnqueueStatus Status, BuildJob Job) Enqueue(BuildJobRequest request)
    {
        Requests.Add(request);
        return (Busy ? EnqueueStatus.RegionBusy : EnqueueStatus.Accepted, new BuildJob(request));
    }
}

public class WatcherTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static (Watcher, FakeProbes, FakeSink) Make(params string[] regions)
    {
        var probes = new FakeProbes();
        var sink = new FakeSink();
        var options = new WatchOptions { Jitter = 0, Regions = regions.Length > 0 ? regions : new WatchOptions().Regions };
        return (new Watcher(probes, sink, options, NullLogger<Watcher>.Instance), probes, sink);
    }

    [Fact]
    public void NothingNewMeansNoJobs()
    {
        var (w, _, sink) = Make();
        var next = w.Tick(T0);
        Assert.Empty(sink.Requests);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), next);   // 官网 10 分钟最早
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), w.NextAt("jp", Watcher.QooApp));
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), w.NextAt("jp", Watcher.GooglePlay));
        Assert.Null(w.NextAt("cn", Watcher.QooApp));         // cn 只有官网
    }

    [Fact]
    public void OfficialNewerVersionCodeTriggersOnce()
    {
        var (w, probes, sink) = Make("cn");
        probes.Official = 4700;
        w.Tick(T0);
        var r = Assert.Single(sink.Requests);
        Assert.Equal("cn", r.Region);
        Assert.Equal("official", r.Source);
        Assert.Equal(4700, r.ExpectedVersionCode);
        Assert.Null(r.Callback);
        Assert.True(r.IsAuto);
        Assert.Null(r.Validate());
        // latest.json 还没刷新：冷却期内不重复发起
        w.Tick(T0 + TimeSpan.FromMinutes(10));
        Assert.Single(sink.Requests);
        // 冷却期过了还没更新（比如上次失败了）：再发一次
        w.Tick(T0 + TimeSpan.FromMinutes(130));
        Assert.Equal(2, sink.Requests.Count);
    }

    [Fact]
    public void QooAppNewerVersion()
    {
        var (w, probes, sink) = Make("jp");
        probes.Qoo["jp"] = new("7.2.0", 300140, false);
        w.Tick(T0);
        Assert.Empty(sink.Requests);                          // 还没准备好
        probes.Qoo["jp"] = new("7.2.0", 300140, true);
        w.Tick(T0 + TimeSpan.FromMinutes(15));
        var r = Assert.Single(sink.Requests);
        Assert.Equal(("jp", "qooapp", 300140L), (r.Region, r.Source, r.ExpectedVersionCode!.Value));
    }

    [Fact]
    public void GooglePlayAheadOfQooApp()
    {
        var (w, probes, sink) = Make("hmt");
        probes.Play["hmt"] = "6.5.0";
        w.Tick(T0);
        Assert.Empty(sink.Requests);
        // Google Play 发现后立即再查 QooApp
        Assert.Equal(T0, w.NextAt("hmt", Watcher.QooApp));
        var calls = probes.QooCalls;
        w.Tick(T0 + TimeSpan.FromSeconds(1));
        Assert.Equal(calls + 1, probes.QooCalls);
        Assert.Empty(sink.Requests);
        // QooApp 没跟上：10 分钟、20 分钟……退避
        Assert.Equal(T0 + TimeSpan.FromSeconds(1) + TimeSpan.FromMinutes(10), w.NextAt("hmt", Watcher.QooApp));
        w.Tick(T0 + TimeSpan.FromMinutes(11));
        Assert.Equal(T0 + TimeSpan.FromMinutes(11) + TimeSpan.FromMinutes(20), w.NextAt("hmt", Watcher.QooApp));
        // QooApp 跟上了：发起任务，恢复正常间隔
        probes.Qoo["hmt"] = new("6.5.0", 210, true);
        w.Tick(T0 + TimeSpan.FromMinutes(31));
        var r = Assert.Single(sink.Requests);
        Assert.Equal(("hmt", "qooapp", 210L), (r.Region, r.Source, r.ExpectedVersionCode!.Value));
        Assert.Equal(T0 + TimeSpan.FromMinutes(31) + TimeSpan.FromMinutes(15), w.NextAt("hmt", Watcher.QooApp));
        // 任务还在跑、latest.json 没更新：Google Play 不再反复把 QooApp 拉到现在，也不重复发起
        w.Tick(T0 + TimeSpan.FromMinutes(46));
        Assert.Single(sink.Requests);
        Assert.Equal(T0 + TimeSpan.FromMinutes(46) + TimeSpan.FromMinutes(15), w.NextAt("hmt", Watcher.QooApp));
        // 发布完成，latest.json 跟上：一切恢复平静
        probes.Latests["hmt"] = new("6.5.0", 210);
        w.Tick(T0 + TimeSpan.FromMinutes(61));
        w.Tick(T0 + TimeSpan.FromHours(5));
        Assert.Single(sink.Requests);
    }

    [Fact]
    public void SourceFailureBacksOffIndependently()
    {
        var (w, probes, sink) = Make("kr");
        probes.QooError = new HttpRequestException("QooApp 超时");
        w.Tick(T0);
        Assert.Equal(T0 + TimeSpan.FromMinutes(30), w.NextAt("kr", Watcher.QooApp));      // 15 分钟 × 2
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), w.NextAt("kr", Watcher.GooglePlay));  // 不受影响
        for (var i = 0; i < 10; i++)
            w.Tick(w.NextAt("kr", Watcher.QooApp)!.Value);
        var last = w.NextAt("kr", Watcher.QooApp)!.Value;
        w.Tick(last);
        Assert.Equal(last + TimeSpan.FromHours(2), w.NextAt("kr", Watcher.QooApp));        // 最长 2 小时
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public void RegionBusyStillCoolsDown()
    {
        var (w, probes, sink) = Make("cn");
        sink.Busy = true;
        probes.Official = 4700;
        w.Tick(T0);
        w.Tick(T0 + TimeSpan.FromMinutes(10));
        Assert.Single(sink.Requests);
    }
}

public class CallbackRuleTests
{
    private static JobQueue Queue(string? syncUrl) => new(new FakeRunner(), null!,
        new ServiceOptions { HmacSecret = new byte[32], SyncCallbackUrl = syncUrl }, NullLogger<JobQueue>.Instance);

    private static BuildJob Job(string id, string? callback, string? result)
    {
        var job = new BuildJob(new BuildJobRequest(id, "jp", null, null, callback));
        if (result is not null)
            job.Outcome = new JobOutcome(result, "7.2.0", 1, null, null, new Dictionary<string, string>());
        return job;
    }

    [Fact]
    public void ExplicitCallbackWins()
    {
        var q = Queue("http://sync/cb");
        Assert.Equal("http://other/cb", q.CallbackTarget(Job("sync-jp-1", "http://other/cb", "noop")));
        Assert.Equal("http://sync/cb", q.CallbackTarget(Job("sync-jp-1", null, "noop")));
        Assert.Null(Queue(null).CallbackTarget(Job("sync-jp-1", null, "advanced")));
    }

    [Fact]
    public void AutoJobsOnlyNotifyOnAdvanced()
    {
        var q = Queue("http://sync/cb");
        Assert.Equal("http://sync/cb", q.CallbackTarget(Job("auto-jp-qooapp-1", null, "advanced")));
        Assert.Null(q.CallbackTarget(Job("auto-jp-qooapp-1", null, "noop")));
        Assert.Null(q.CallbackTarget(Job("auto-jp-qooapp-1", null, null)));   // 失败
    }
}
