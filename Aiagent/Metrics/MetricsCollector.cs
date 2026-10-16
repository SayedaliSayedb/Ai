using System.Collections.Concurrent;
using Aiagent.Models;

namespace Aiagent.Services.Metrics;

public interface IMetricsCollector
{
    void RecordTtft(double ms);
    void RecordTpot(double ms);
    void RecordTokens(int count);
    void RecordNoKvSlot();
    void RecordTimeout();
    void RecordBatch();
    MetricSample Snapshot();
    event Action<MetricSample>? OnSample;
}

public class MetricsCollector : IMetricsCollector, IDisposable
{
    private const int WindowSize = 500; // نمونه‌های نگهداری‌شده برای محاسبه‌ی صدک

    private readonly ConcurrentQueue<double> _ttft = new();
    private readonly ConcurrentQueue<double> _tpot = new();
    private readonly ConcurrentQueue<(long TimestampMs, int Count)> _tokensPerSecWindow = new();
    private readonly ConcurrentQueue<long> _batchesPerSecWindow = new();

    private int _noKvSlotCount;
    private int _timeoutCount;
    private int _activeConversations;
    private int _queuedRequests;
    private long _totalTokens;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private readonly Timer _sampler;
    private readonly ILogger<MetricsCollector> _logger;
    private readonly IGpuMonitor _gpu;

    // برای هماهنگی با ChatCoordinator
    public Func<int>? ActiveConversationsProvider { get; set; }
    public Func<int>? ActiveSequencesProvider { get; set; }
    public Func<int>? MaxSequencesProvider { get; set; }
    public Func<int>? QueuedRequestsProvider { get; set; }
    public Func<double>? KvCacheUsageProvider { get; set; }

    public event Action<MetricSample>? OnSample;

    public MetricsCollector(IGpuMonitor gpu, ILogger<MetricsCollector> logger)
    {
        _gpu = gpu;
        _logger = logger;
        _sampler = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void RecordTtft(double ms)
    {
        _ttft.Enqueue(ms);
        TrimQueue(_ttft);
    }

    public void RecordTpot(double ms)
    {
        _tpot.Enqueue(ms);
        TrimQueue(_tpot);
    }

    public void RecordTokens(int count)
    {
        if (count <= 0)
            return;

        Interlocked.Add(ref _totalTokens, count);
        _tokensPerSecWindow.Enqueue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), count));
    }

    public void RecordNoKvSlot() => Interlocked.Increment(ref _noKvSlotCount);
    public void RecordTimeout() => Interlocked.Increment(ref _timeoutCount);

    public void RecordBatch()
        => _batchesPerSecWindow.Enqueue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public MetricSample Snapshot()
    {
        var ttftArr = _ttft.ToArray();
        var tpotArr = _tpot.ToArray();
        var gpu = _gpu.Read();

        var sample = new MetricSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            TtftP50 = Percentile(ttftArr, 50),
            TtftP95 = Percentile(ttftArr, 95),
            TtftP99 = Percentile(ttftArr, 99),
            TpotP50 = Percentile(tpotArr, 50),
            TpotP95 = Percentile(tpotArr, 95),
            TpotP99 = Percentile(tpotArr, 99),
            NoKvSlotCount = _noKvSlotCount,
            TimeoutCount = _timeoutCount,
            GpuUtilizationPercent = gpu?.UtilizationPercent ?? 0,
            GpuMemoryUsedMb = gpu?.MemoryUsedMb ?? 0,
            GpuMemoryTotalMb = gpu?.MemoryTotalMb ?? 0,
            GpuTemperature = gpu?.Temperature ?? 0,
            ProcessRamMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0),

            // مقادیر از ChatCoordinator
            ActiveConversations = ActiveConversationsProvider?.Invoke() ?? 0,
            ActiveSequences = ActiveSequencesProvider?.Invoke() ?? 0,
            MaxSequences = MaxSequencesProvider?.Invoke() ?? 0,
            QueuedRequests = QueuedRequestsProvider?.Invoke() ?? 0,
            KvCacheUsagePercent = KvCacheUsageProvider?.Invoke() ?? 0
        };

        // محاسبه‌ی نرخ توکن و بچ در ۵ ثانیه‌ی اخیر
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // شمارش تعداد واقعی توکن‌های ثبت‌شده در پنجره، نه تعداد رویدادهای ثبت.
        // پنجره‌ی ۵ ثانیه‌ای باعث کاهش نوسان نرخ نمایش داده‌شده می‌شود.
        var tokenEvents = _tokensPerSecWindow.ToArray();
        var tokensInWindow = tokenEvents
            .Where(e => now - e.TimestampMs < 5000)
            .Sum(e => (long)e.Count);
        var elapsedWindowSeconds = Math.Clamp(
            (DateTimeOffset.UtcNow - _startTime).TotalSeconds, 0.1, 5.0);
        sample.TokensPerSecond = tokensInWindow / elapsedWindowSeconds;

        sample.BatchesPerSecond =
            _batchesPerSecWindow.Count(t => now - t < 5000) / elapsedWindowSeconds;

        // حذف رویدادهای قدیمی از پنجره
        while (_tokensPerSecWindow.TryPeek(out var tokenEvent) && now - tokenEvent.TimestampMs > 10000)
            _tokensPerSecWindow.TryDequeue(out _);
        while (_batchesPerSecWindow.TryPeek(out var batchTimestamp) && now - batchTimestamp > 10000)
            _batchesPerSecWindow.TryDequeue(out _);
        sample.SloViolated = sample.TtftP95 > 2000 || sample.TpotP95 > 500;

        return sample;
    }

    private void Sample()
    {
        try
        {
            var s = Snapshot();
            OnSample?.Invoke(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در نمونه‌برداری متریک");
        }
    }

    private static void TrimQueue(ConcurrentQueue<double> q)
    {
        while (q.Count > WindowSize) q.TryDequeue(out _);
    }

    private static double Percentile(double[] values, int p)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        var rank = (p / 100.0) * (values.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return values[lo];
        return values[lo] + (values[hi] - values[lo]) * (rank - lo);
    }

    public void Dispose() => _sampler.Dispose();
}