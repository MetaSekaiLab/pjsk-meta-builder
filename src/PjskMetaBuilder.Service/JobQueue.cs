using System.Text.Json;
using System.Threading.Channels;

namespace PjskMetaBuilder.Service;

public enum EnqueueStatus { Accepted, Duplicate, RegionBusy }

/// <summary>
/// 任务队列（§7.4）：同一时间只跑一个任务（内存考虑），其余排队。
/// - 同一 requestId 重复提交：幂等，返回已有任务；
/// - 同一地区已有任务排队或在跑：拒绝（409），返回那个任务的 requestId，同步服务等它的回调即可。
/// 任务状态只在内存里，重启后丢失。
/// </summary>
public sealed partial class JobQueue(IJobRunner runner, CallbackSender callbacks, ServiceOptions options, ILogger<JobQueue> logger)
    : BackgroundService, IJobSink
{
    private readonly Channel<BuildJob> _channel = Channel.CreateUnbounded<BuildJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, BuildJob> _jobs = new(StringComparer.Ordinal);
    private readonly Queue<string> _finishedOrder = new();
    private readonly Lock _lock = new();

    public int Pending { get { lock (_lock) return _jobs.Values.Count(j => j.Status == JobStatus.Queued); } }
    public BuildJob? Running { get { lock (_lock) return _jobs.Values.FirstOrDefault(j => j.Status == JobStatus.Running); } }

    public (EnqueueStatus Status, BuildJob Job) Enqueue(BuildJobRequest request)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(request.RequestId, out var existing))
                return (EnqueueStatus.Duplicate, existing);
            var busy = _jobs.Values.FirstOrDefault(j => j.Request.Region == request.Region
                && j.Status is JobStatus.Queued or JobStatus.Running);
            if (busy is not null)
                return (EnqueueStatus.RegionBusy, busy);
            var job = new BuildJob(request);
            _jobs[request.RequestId] = job;
            _channel.Writer.TryWrite(job);
            return (EnqueueStatus.Accepted, job);
        }
    }

    public BuildJob? Get(string requestId)
    {
        lock (_lock)
            return _jobs.GetValueOrDefault(requestId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_lock)
            {
                job.Status = JobStatus.Running;
                job.StartedAt = DateTimeOffset.UtcNow;
            }
            logger.LogInformation("[{RequestId}] 开始：{Region} source={Source} expected={Expected}",
                job.Request.RequestId, job.Request.Region, job.Request.Source ?? "默认", job.Request.ExpectedVersionCode);
            try
            {
                // 流水线是同步、CPU/IO 密集的代码：放到线程池，不阻塞宿主
                var outcome = await Task.Run(() => runner.Run(job.Request, stoppingToken), stoppingToken);
                lock (_lock)
                {
                    job.Outcome = outcome;
                    job.Status = JobStatus.Succeeded;
                }
                logger.LogInformation("[{RequestId}] 完成：{Result} {Path} {Commit}", job.Request.RequestId, outcome.Result, outcome.Path, outcome.Commit);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                var kind = ErrorKinds.Classify(e);
                lock (_lock)
                {
                    job.Error = new JobError(kind, e.Message);
                    job.Status = JobStatus.Failed;
                }
                if (kind == ErrorKinds.Internal)
                    logger.LogError(e, "[{RequestId}] 失败（{Kind}）", job.Request.RequestId, kind);
                else
                    logger.LogWarning("[{RequestId}] 失败（{Kind}）：{Message}", job.Request.RequestId, kind, e.Message);
            }
            finally
            {
                lock (_lock)
                {
                    job.FinishedAt = DateTimeOffset.UtcNow;
                    job.CallbackState = "pending";
                    _finishedOrder.Enqueue(job.Request.RequestId);
                    while (_finishedOrder.Count > options.KeepFinishedJobs)
                        _jobs.Remove(_finishedOrder.Dequeue());
                }
            }
            // 回调在后台重试，不占用下一个任务（产物已在仓库，回调只是通知）
            var target = CallbackTarget(job);
            if (target is null)
            {
                lock (_lock)
                    job.CallbackState = "skipped";
                continue;
            }
            _ = Task.Run(async () =>
            {
                var delivered = await callbacks.SendAsync(target, CallbackPayload.From(job), stoppingToken);
                lock (_lock)
                    job.CallbackState = delivered ? "delivered" : "gave_up";
            }, stoppingToken);
        }
    }
}

public sealed partial class JobQueue
{
    /// <summary>
    /// 回调地址（§7.6）：请求里给了就用请求里的，否则用 SYNC_CALLBACK_URL；都没有就不回调。
    /// 轮询器自己发起的任务（auto-）只在发布结果为 advanced 时通知同步服务（不是它请求的，其他结果它不关心）。
    /// </summary>
    public string? CallbackTarget(BuildJob job)
    {
        if (job.Request.IsAuto && job.Outcome?.Result != "advanced")
            return null;
        return job.Request.Callback ?? options.SyncCallbackUrl;
    }
}

/// <summary>回调（§7.2）：带签名 POST，失败按配置的间隔重试，之后放弃（同步服务会低频检查仓库兜底）。</summary>
public sealed class CallbackSender(HttpClient http, ServiceOptions options, ILogger<CallbackSender> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<bool> SendAsync(string url, CallbackPayload payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new("application/json");
                req.Headers.Add(Hmac.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                req.Headers.Add(Hmac.SignatureHeader, Hmac.Sign(options.HmacSecret, ts, body));
                using var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    logger.LogInformation("[{RequestId}] 回调成功（第 {Attempt} 次）", payload.RequestId, attempt + 1);
                    return true;
                }
                logger.LogWarning("[{RequestId}] 回调返回 HTTP {Status}", payload.RequestId, (int)resp.StatusCode);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("[{RequestId}] 回调失败：{Message}", payload.RequestId, e.Message);
            }
            if (attempt >= options.CallbackRetryDelays.Count)
            {
                logger.LogWarning("[{RequestId}] 回调重试 {Count} 次后放弃", payload.RequestId, attempt);
                return false;
            }
            await Task.Delay(options.CallbackRetryDelays[attempt], ct);
        }
    }
}
