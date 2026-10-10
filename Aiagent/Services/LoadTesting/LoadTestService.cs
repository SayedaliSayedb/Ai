using Aiagent.Services.Scheduling;
using Microsoft.AspNetCore.SignalR.Client;
using System.Collections.Concurrent;

namespace Aiagent.Services.LoadTesting;

public sealed class LoadTestService
{
    private readonly ILogger<LoadTestService> _logger;
    private readonly SchedulingState _schedState;
    private readonly KvCachePolicyState _kvState;
    private readonly ChunkedPrefillState _chunkedState;

    private readonly ConcurrentDictionary<string, LoadTestRun> _runs = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public LoadTestService(
        ILogger<LoadTestService> logger,
        SchedulingState schedState,
        KvCachePolicyState kvState,
        ChunkedPrefillState chunkedState)
    {
        _logger = logger;
        _schedState = schedState;
        _kvState = kvState;
        _chunkedState = chunkedState;
    }

    public LoadTestRun? GetRun(string runId)
        => _runs.TryGetValue(runId, out var r) ? r : null;

    public IReadOnlyList<LoadTestRun> GetAllRuns()
        => _runs.Values.OrderByDescending(r => r.StartedAt).ToList();

    public async Task<LoadTestRun> StartAsync(
        LoadTestRequest request, string baseUrl, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            // 1) Snapshot وضعیت تاگل‌ها
            var prevSched = _schedState.Enabled;
            var prevKv = _kvState.Enabled;
            var prevChunk = _chunkedState.Enabled;

            // 2) اعمال تاگل‌های درخواستی
            _schedState.Enabled = request.EnableScheduling;
            _kvState.Enabled = request.EnableKvCachePolicy;
            _chunkedState.Enabled = request.EnableChunkedPrefill;

            // 3) پرامپت‌ها
            var prompts = request.Prompts.Count > 0
                ? request.Prompts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
                : new List<string> { "Hello, introduce yourself briefly." };

            var total = request.Concurrency * request.RequestsPerUser;

            var run = new LoadTestRun
            {
                RunId = Guid.NewGuid().ToString("N")[..10],
                Request = request,
                TotalRequests = total
            };

            _runs[run.RunId] = run;

            _logger.LogInformation(
                "🧪 LoadTest {RunId} شروع شد | variant={V} | concurrency={C} | req/user={R} | total={T} | sched={S} kv={K} chunk={Ch}",
                run.RunId, request.VariantName, request.Concurrency,
                request.RequestsPerUser, total,
                request.EnableScheduling, request.EnableKvCachePolicy,
                request.EnableChunkedPrefill);

            // 4) اجرای async (background)
            _ = Task.Run(async () =>
            {
                // ⬅️ [FIX] watchdog کلی: ۳۰ ثانیه بیشتر از timeout یک درخواست
                using var overallCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(request.TimeoutSeconds + 30));

                try
                {
                    var tasks = Enumerable.Range(0, request.Concurrency)
                        .Select(userIndex => RunVirtualUserAsync(
                            run, userIndex, baseUrl, prompts, overallCts.Token))
                        .ToArray();

                    var allTasks = Task.WhenAll(tasks);
                    var completed = await Task.WhenAny(
                        allTasks,
                        Task.Delay(Timeout.Infinite, overallCts.Token));

                    if (completed != allTasks)
                    {
                        _logger.LogWarning(
                            "⏰ LoadTest {RunId}: overall timeout ({T}s) — {Done}/{Total} درخواست کامل شد",
                            run.RunId, request.TimeoutSeconds + 30,
                            run.CompletedRequests, run.TotalRequests);
                    }
                    else
                    {
                        await allTasks;   // پراپاگیت استثناها
                    }

                    run.Status = LoadTestStatus.Completed;
                }
                catch (Exception ex)
                {
                    run.Status = LoadTestStatus.Failed;
                    run.Error = ex.Message;
                    _logger.LogError(ex, "LoadTest {RunId} با خطا مواجه شد", run.RunId);
                }
                finally
                {
                    run.CompletedAt = DateTimeOffset.UtcNow;

                    _schedState.Enabled = prevSched;
                    _kvState.Enabled = prevKv;
                    _chunkedState.Enabled = prevChunk;

                    _logger.LogInformation(
                        "🧪 LoadTest {RunId} پایان یافت | status={S} | completed={C}/{T}",
                        run.RunId, run.Status, run.CompletedRequests, run.TotalRequests);
                }
            }, CancellationToken.None);

            return run;
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ========================================================
    // یک virtual user: چند درخواست متوالی
    // ========================================================
    private async Task RunVirtualUserAsync(
        LoadTestRun run, int userIndex, string baseUrl,
        List<string> prompts, CancellationToken ct)
    {
        for (int i = 0; i < run.Request.RequestsPerUser; i++)
        {
            var prompt = prompts[(userIndex + i) % prompts.Count];
            var result = await RunSingleRequestAsync(
                userIndex, i, prompt, baseUrl, run.Request, ct);

            lock (run.Results) run.Results.Add(result);
            Interlocked.Increment(ref run.CompletedRequests);
        }
    }

    private async Task<LoadTestResult> RunSingleRequestAsync(
    int userIndex, int requestIndex, string prompt,
    string baseUrl, LoadTestRequest request, CancellationToken ct)
    {
        var sentAt = DateTimeOffset.UtcNow;
        var result = new LoadTestResult
        {
            UserIndex = userIndex,
            RequestIndex = requestIndex,
            Prompt = prompt,
            SentAt = sentAt
        };

        HubConnection? conn = null;
        var firstChunkAt = new TaskCompletionSource<DateTimeOffset>();
        var completeAt = new TaskCompletionSource<DateTimeOffset>();
        var chunkTimestamps = new List<DateTimeOffset>();
        string? errorMsg = null;

        // ⬅️ [FIX] timeoutCts قبل از StartAsync/InvokeAsync ساخته شود
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            conn = new HubConnectionBuilder()
                .WithUrl($"{baseUrl}/chatHub")
                .Build();

            conn.On<string>("ReceiveUserMessage", _ => { });
            conn.On<string>("ReceiveBotChunk", _ =>
            {
                var now = DateTimeOffset.UtcNow;
                if (chunkTimestamps.Count == 0) firstChunkAt.TrySetResult(now);
                chunkTimestamps.Add(now);
            });
            conn.On("StreamComplete", () => completeAt.TrySetResult(DateTimeOffset.UtcNow));
            conn.On<string>("ReceiveError", err =>
            {
                errorMsg = err;
                completeAt.TrySetResult(DateTimeOffset.UtcNow);
            });

            await conn.StartAsync(timeoutCts.Token);

            var sendTime = DateTimeOffset.UtcNow;
            result.SentAt = sendTime;

            // ⬅️ [FIX] InvokeAsync با timeout
            await conn.InvokeAsync("SendMessage", prompt, timeoutCts.Token);

            // ⬅️ [FIX] منتظر StreamComplete با timeout
            try
            {
                await completeAt.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timeout after {request.TimeoutSeconds}s (chunks={chunkTimestamps.Count})");
            }

            var endTime = completeAt.Task.Result;
            result.E2EMs = (endTime - sendTime).TotalMilliseconds;
            result.ChunkCount = chunkTimestamps.Count;

            if (firstChunkAt.Task.IsCompletedSuccessfully)
            {
                var firstTime = firstChunkAt.Task.Result;
                result.TtftMs = (firstTime - sendTime).TotalMilliseconds;

                result.TpotMs = chunkTimestamps.Count > 1
                    ? (endTime - firstTime).TotalMilliseconds / (chunkTimestamps.Count - 1)
                    : 0;
            }

            // ⬅️ [FIX] تشخیص شکست
            if (!string.IsNullOrEmpty(errorMsg))
            {
                result.Success = false;
                result.ErrorKind = ClassifyError(errorMsg);
                result.ErrorMessage = errorMsg;
            }
            else if (chunkTimestamps.Count < 5)
            {
                result.Success = false;
                result.ErrorKind = "truncated";
                result.ErrorMessage = $"Only {chunkTimestamps.Count} chunks received";
            }
            else
            {
                result.Success = true;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorKind = ClassifyError(ex.Message);
            result.ErrorMessage = ex.Message;
            result.E2EMs = (DateTimeOffset.UtcNow - sentAt).TotalMilliseconds;
        }
        finally
        {
            if (conn != null)
            {
                try { await conn.StopAsync(); } catch { }
                try { await conn.DisposeAsync(); } catch { }
            }
        }

        return result;
    }

    private static string ClassifyError(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return "unknown";
        var m = msg.ToLowerInvariant();
        if (m.Contains("timeout")) return "timeout";
        if (m.Contains("nokv") || m.Contains("kv cache")) return "nokvslot";
        if (m.Contains("queue") || m.Contains("صف")) return "queue_full";
        if (m.Contains("cancel")) return "cancelled";
        // ⬅️ [FIX] پیام فارسی
        if (m.Contains("حافظه") || m.Contains("فشار")) return "kv_cache_pressure";
        return "other";
    }

    // ========================================================
    // خلاصه
    // ========================================================
    public LoadTestSummary? GetSummary(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run)) return null;
        return BuildSummary(run);
    }

    private static LoadTestSummary BuildSummary(LoadTestRun run)
    {
        LoadTestResult[] results;
        lock (run.Results) results = run.Results.ToArray();

        var successful = results.Where(r => r.Success).ToArray();
        var failed = results.Where(r => !r.Success).ToArray();
        var dur = (run.CompletedAt ?? DateTimeOffset.UtcNow) - run.StartedAt;
        var totalTokens = successful.Sum(r => r.ChunkCount);

        return new LoadTestSummary
        {
            RunId = run.RunId,
            VariantName = run.Request.VariantName,
            Concurrency = run.Request.Concurrency,
            TotalRequests = results.Length,
            Successful = successful.Length,
            Failed = failed.Length,
            DurationSeconds = Math.Round(dur.TotalSeconds, 2),
            ThroughputRps = dur.TotalSeconds > 0
                ? Math.Round(results.Length / dur.TotalSeconds, 2) : 0,
            ThroughputTps = dur.TotalSeconds > 0
                ? Math.Round(totalTokens / dur.TotalSeconds, 2) : 0,
            Ttft = PercentilesFrom(successful.Where(r => r.TtftMs > 0)
                .Select(r => r.TtftMs).ToArray()),
            Tpot = PercentilesFrom(successful.Where(r => r.TpotMs > 0)
                .Select(r => r.TpotMs).ToArray()),
            E2E = PercentilesFrom(results.Where(r => r.E2EMs > 0)
                .Select(r => r.E2EMs).ToArray()),
            ErrorsByKind = failed
                .GroupBy(r => r.ErrorKind ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private static Percentiles PercentilesFrom(double[] values)
    {
        if (values.Length == 0) return new Percentiles(0, 0, 0, 0, 0);
        Array.Sort(values);
        return new Percentiles(
            Math.Round(P(values, 50), 1),
            Math.Round(P(values, 95), 1),
            Math.Round(P(values, 99), 1),
            Math.Round(values.Average(), 1),
            Math.Round(values[^1], 1));

        static double P(double[] sorted, int p)
        {
            var rank = (p / 100.0) * (sorted.Length - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
        }
    }
}