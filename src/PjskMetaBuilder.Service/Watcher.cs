using System.Text.Json;
using PjskMetaBuilder.Core;
using PjskMetaBuilder.Core.Sources;
using PjskMetaBuilder.Core.Util;

namespace PjskMetaBuilder.Service;

/// <summary>产物仓库里某区服 latest.json 的版本。</summary>
public sealed record LatestVersion(string AppVersion, long VersionCode);

/// <summary>轮询器用到的探测（测试可替换）。每个方法只发很少的请求，不下载安装包。</summary>
public interface IVersionProbes
{
    LatestVersion? Latest(string region);
    long? OfficialVersionCode(RegionInfo region);
    QooAppVersion QooApp(RegionInfo region);
    GooglePlayInfo GooglePlay(RegionInfo region);
}

/// <summary>轮询器往队列里放任务（<see cref="JobQueue"/> 实现）。</summary>
public interface IJobSink
{
    (EnqueueStatus Status, BuildJob Job) Enqueue(BuildJobRequest request);
}

/// <summary>
/// 主动发现新安装包（设计文档 §7.5，M6）：按区服、按来源定时检查，和产物仓库的 latest.json 比较，
/// 有更新就往队列里放一个 <c>auto-</c> 任务（与同步服务的请求走同一个队列：同一地区不重复、同一时间只跑一个）。
/// builder 自己不存状态，基准永远是产物仓库；内存里只有调度时间、退避和最近触发过的版本（防止 latest.json 缓存未刷新时重复下载）。
/// </summary>
public sealed class Watcher(IVersionProbes probes, IJobSink jobs, WatchOptions options, ILogger<Watcher> logger)
{
    public const string Official = "official", QooApp = "qooapp", GooglePlay = "googleplay";

    private sealed class SourceState
    {
        public DateTimeOffset NextAt;
        public int Failures;
    }

    private readonly Dictionary<(string Region, string Source), SourceState> _states = [];
    private readonly Dictionary<(string Region, long VersionCode), DateTimeOffset> _triggered = [];
    // Google Play 已有新版本、QooApp 还没跟上：region → (Google Play 上的版本, 当前退避)
    private readonly Dictionary<string, (string Version, TimeSpan Backoff)> _ahead = [];
    private readonly Random _random = new();
    private readonly Dictionary<(string Region, string Source), string> _seen = [];

    /// <summary>某个来源观察到的版本第一次出现或变化时记一条日志（平时安静）。</summary>
    private void Seen(string region, string source, string observed, LatestVersion? latest)
    {
        if (_seen.TryGetValue((region, source), out var old) && old == observed)
            return;
        _seen[(region, source)] = observed;
        logger.LogInformation("轮询 {Region}/{Source}：{Observed}（latest {Latest}）", region, source, observed, Describe(latest));
    }

    /// <summary>要检查的 (区服, 来源)。</summary>
    public IEnumerable<(string Region, string Source)> Targets()
    {
        foreach (var code in options.Regions)
        {
            var r = Regions.Get(code);
            if (r.OfficialUrl is not null)
                yield return (code, Official);
            if (r.QooAppId is not null)
                yield return (code, QooApp);
            if (GooglePlayPage.Storefronts.ContainsKey(code))
                yield return (code, GooglePlay);
        }
    }

    /// <summary>跑所有到期的检查，返回下一次最早到期的时间。</summary>
    public DateTimeOffset Tick(DateTimeOffset now)
    {
        foreach (var key in Targets())
        {
            if (!_states.TryGetValue(key, out var st))
                _states[key] = st = new SourceState { NextAt = now };
            if (st.NextAt > now)
                continue;
            try
            {
                var custom = Check(key.Region, key.Source, now);
                st.Failures = 0;
                // Check 可能已把别的来源（QooApp）提前到现在：只改自己的
                st.NextAt = now + Jitter(custom ?? Interval(key.Source));
            }
            catch (Exception e)
            {
                st.Failures++;
                var delay = Min(Interval(key.Source) * Math.Pow(2, Math.Min(st.Failures, 10)), options.MaxBackoff);
                st.NextAt = now + Jitter(delay);
                var kind = ErrorKinds.Classify(e);
                logger.LogWarning("轮询 {Region}/{Source} 失败（{Kind}，连续 {Failures} 次），{Delay} 后再试：{Message}",
                    key.Region, key.Source, kind, st.Failures, delay, e.Message);
            }
        }
        return _states.Values.Min(s => s.NextAt);
    }

    /// <summary>检查一个来源；返回非空时表示下次检查用这个间隔（QooApp 等 Google Play 时的退避）。</summary>
    private TimeSpan? Check(string code, string source, DateTimeOffset now)
    {
        var region = Regions.Get(code);
        var latest = probes.Latest(code);
        switch (source)
        {
            case Official:
                if (probes.OfficialVersionCode(region) is not { } vc)
                {
                    logger.LogWarning("官网 {Region}：文件名里没有 versionCode，跳过", code);
                    return null;
                }
                Seen(code, Official, $"versionCode {vc}", latest);
                if (latest is null || vc > latest.VersionCode)
                    Trigger(code, Official, vc, $"官网 versionCode {vc}，latest {Describe(latest)}", now);
                return null;

            case QooApp:
            {
                var q = probes.QooApp(region);
                Seen(code, QooApp, $"{q.VersionName}（{q.VersionCode}）{(q.Ready ? "" : "，未就绪")}", latest);
                var newer = latest is null || q.VersionCode > latest.VersionCode
                    || (AppVersion.IsValid(q.VersionName) && AppVersion.Compare(q.VersionName, latest.AppVersion) > 0);
                if (newer && q.Ready)
                {
                    // 不清除 _ahead：latest.json 要等任务发布后才更新，清掉的话 Google Play 会再次判为领先
                    Trigger(code, QooApp, q.VersionCode, $"QooApp {q.VersionName}（versionCode {q.VersionCode}），latest {Describe(latest)}", now);
                    return null;
                }
                if (newer)
                {
                    logger.LogInformation("QooApp {Region}：{Version} 还没准备好", code, q.VersionName);
                    return null;
                }
                if (!_ahead.TryGetValue(code, out var ahead))
                    return null;
                if (latest is not null && AppVersion.Compare(ahead.Version, latest.AppVersion) <= 0)
                {
                    _ahead.Remove(code);   // 产物仓库已经跟上（比如同步服务先请求了）
                    return null;
                }
                // Google Play 已经有了，QooApp 还没跟上：10 分钟起翻倍，最长 2 小时
                _ahead[code] = (ahead.Version, Min(ahead.Backoff * 2, options.MaxBackoff));
                logger.LogInformation("{Region}：Google Play 已有 {Version}，QooApp 还是 {Qoo}，{Delay} 后再看",
                    code, ahead.Version, q.VersionName, ahead.Backoff);
                return ahead.Backoff;
            }

            case GooglePlay:
            {
                var g = probes.GooglePlay(region);
                Seen(code, GooglePlay, g.VersionName, latest);
                if (latest is not null && AppVersion.Compare(g.VersionName, latest.AppVersion) <= 0)
                    return null;
                if (_ahead.TryGetValue(code, out var known) && known.Version == g.VersionName)
                    return null;
                // 提前信号：下载仍走 QooApp。马上查一次 QooApp，没跟上就进入退避
                logger.LogInformation("Google Play {Region}：{Version}（{Updated}），latest {Latest}；立即检查 QooApp",
                    code, g.VersionName, g.UpdatedAt, Describe(latest));
                _ahead[code] = (g.VersionName, options.AheadBackoff);
                if (_states.TryGetValue((code, QooApp), out var qs))
                    qs.NextAt = now;
                return null;
            }
        }
        return null;
    }

    private void Trigger(string region, string source, long versionCode, string reason, DateTimeOffset now)
    {
        if (_triggered.TryGetValue((region, versionCode), out var at) && now - at < options.Cooldown)
            return;   // 刚触发过：可能任务还在跑，或 latest.json 的缓存还没刷新
        var request = new BuildJobRequest($"auto-{region}-{source}-{now.ToUnixTimeSeconds()}", region, source, versionCode, null);
        var (status, job) = jobs.Enqueue(request);
        _triggered[(region, versionCode)] = now;
        foreach (var old in _triggered.Where(kv => now - kv.Value > options.Cooldown).Select(kv => kv.Key).ToList())
            _triggered.Remove(old);
        if (status == EnqueueStatus.RegionBusy)
            logger.LogInformation("发现 {Region} 新版本（{Reason}），但已有任务 {RequestId}，不重复发起", region, reason, job.Request.RequestId);
        else
            logger.LogInformation("发现 {Region} 新版本（{Reason}），已发起 {RequestId}", region, reason, request.RequestId);
    }

    private TimeSpan Interval(string source) => source switch
    {
        Official => options.OfficialInterval,
        QooApp => options.QooAppInterval,
        _ => options.GooglePlayInterval,
    };

    private TimeSpan Jitter(TimeSpan d) => d * (1 + options.Jitter * (2 * _random.NextDouble() - 1));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Describe(LatestVersion? v) => v is null ? "无" : $"{v.AppVersion}（{v.VersionCode}）";

    /// <summary>导出给测试：某个来源下次检查的时间。</summary>
    public DateTimeOffset? NextAt(string region, string source) =>
        _states.TryGetValue((region, source), out var st) ? st.NextAt : null;
}

public sealed record WatchOptions
{
    public IReadOnlyList<string> Regions { get; init; } = ["jp", "en", "hmt", "kr", "cn"];
    public TimeSpan OfficialInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan QooAppInterval { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan GooglePlayInterval { get; init; } = TimeSpan.FromMinutes(15);
    public double Jitter { get; init; } = 0.1;
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(2);
    /// <summary>Google Play 先于 QooApp 时，QooApp 的第一次退避（之后翻倍）。</summary>
    public TimeSpan AheadBackoff { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>同一 (区服, versionCode) 触发过后，这段时间内不再触发。</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(2);
}

/// <summary>真实探测：latest.json 从 GitHub raw 读（公开仓库，不需要 token；缓存最多几分钟，由 Cooldown 兜底）。</summary>
public sealed class HttpVersionProbes(ServiceOptions options) : IVersionProbes
{
    private static readonly HttpClient Http = Downloader.CreateClient();
    private readonly OfficialUrlSource _official = new(Http);
    private readonly GooglePlayPage _googlePlay = new(Http);
    private QooAppSource? _qooApp;

    public LatestVersion? Latest(string region)
    {
        var uri = new Uri($"https://raw.githubusercontent.com/{options.AppMetaRepo}/main/{region}/latest.json");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd("pjsk-meta-builder");
        using var resp = Http.Send(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"读 {region}/latest.json：HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        var root = doc.RootElement;
        return new LatestVersion(root.GetProperty("appVersion").GetString()!, root.GetProperty("package").GetProperty("versionCode").GetInt64());
    }

    public long? OfficialVersionCode(RegionInfo region) => _official.ProbeVersion(region).VersionCode;

    public QooAppVersion QooApp(RegionInfo region) =>
        (_qooApp ??= new QooAppSource(QooAppCredentials.FromEnvironment(), Http)).Current(region);

    public GooglePlayInfo GooglePlay(RegionInfo region) => _googlePlay.Probe(region);
}

/// <summary>PJSK_WATCH=1 时在后台跑轮询器。</summary>
internal sealed class WatcherService(Watcher watcher, ILogger<WatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("主动发现已启用：{Targets}", string.Join(" ", watcher.Targets().Select(t => $"{t.Region}/{t.Source}")));
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset next;
            try
            {
                next = await Task.Run(() => watcher.Tick(DateTimeOffset.UtcNow), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            var delay = next - DateTimeOffset.UtcNow;
            delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay;
            await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
