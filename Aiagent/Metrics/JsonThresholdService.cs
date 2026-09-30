using Aiagent.Models;
using Aiagent.Services.Storage;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aiagent.Services.Metrics;

public class JsonThresholdService : IThresholdService
{
    private const string ThresholdFile = "thresholds/current.json";
    private const string BreachesFile = "thresholds/breaches.json";
    private const int MaxBreachesInMemory = 500;

    private readonly IStorageService _storage;
    private readonly ILogger<JsonThresholdService> _logger;

    private MetricsThreshold _current = new();
    private readonly ConcurrentQueue<ThresholdBreachRecord> _breaches = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public event Action<ThresholdBreachRecord>? OnBreach;

    public JsonThresholdService(IStorageService storage, ILogger<JsonThresholdService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;

            if (await _storage.ExistsAsync(ThresholdFile))
            {
                var json = await _storage.ReadTextAsync(ThresholdFile);
                _current = JsonSerializer.Deserialize<MetricsThreshold>(json) ?? new();
            }

            if (await _storage.ExistsAsync(BreachesFile))
            {
                var json = await _storage.ReadTextAsync(BreachesFile);
                var list = JsonSerializer.Deserialize<List<ThresholdBreachRecord>>(json) ?? new();
                foreach (var b in list.TakeLast(MaxBreachesInMemory))
                    _breaches.Enqueue(b);
            }

            _loaded = true;
            _logger.LogInformation("✅ آستانه‌ها بارگذاری شدند");
        }
        finally { _lock.Release(); }
    }

    public async Task<MetricsThreshold> GetAsync()
    {
        await EnsureLoadedAsync();
        return _current;
    }

    public async Task SaveAsync(MetricsThreshold threshold)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            threshold.UpdatedAt = DateTimeOffset.UtcNow;
            _current = threshold;
            var json = JsonSerializer.Serialize(threshold,
                new JsonSerializerOptions { WriteIndented = true });
            await _storage.WriteTextAsync(ThresholdFile, json);
            _logger.LogInformation("✅ آستانه‌ها ذخیره شدند: TTFT<{T}ms, TPOT<{P}ms, KV<{K}%",
                threshold.TtftP95Max, threshold.TpotP95Max, threshold.KvCacheMax);
        }
        finally { _lock.Release(); }
    }

    public IReadOnlyList<ThresholdBreachRecord> Evaluate(MetricSample sample)
    {
        var breaches = new List<ThresholdBreachRecord>();

        void Check(string name, double value, double max, string sev)
        {
            if (value <= max) return;
            var b = new ThresholdBreachRecord
            {
                Timestamp = sample.Timestamp.UtcDateTime,
                MetricName = name,
                Value = value,
                Threshold = max,
                Severity = sev,
                ActiveConversations = sample.ActiveConversations,
                GpuUtilization = sample.GpuUtilizationPercent
            };
            breaches.Add(b);
            _breaches.Enqueue(b);
            while (_breaches.Count > MaxBreachesInMemory) _breaches.TryDequeue(out _);

            try { OnBreach?.Invoke(b); } catch { }
        }

        Check("TTFT_p95", sample.TtftP95, _current.TtftP95Max, "Critical");
        Check("TPOT_p95", sample.TpotP95, _current.TpotP95Max, "Critical");
        Check("KV_Cache", sample.KvCacheUsagePercent, _current.KvCacheMax, "Critical");
        Check("GPU_Utilization", sample.GpuUtilizationPercent, _current.GpuUtilizationMax, "Warning");
        Check("GPU_Memory",
              sample.GpuMemoryTotalMb > 0
                ? sample.GpuMemoryUsedMb / sample.GpuMemoryTotalMb * 100
                : 0,
              _current.GpuMemoryMax, "Warning");

        if (breaches.Count > 0)
            _ = PersistBreachesAsync();

        return breaches;
    }

    public async Task<IReadOnlyList<ThresholdBreachRecord>> GetRecentBreachesAsync(int count = 100)
    {
        await EnsureLoadedAsync();
        return _breaches.TakeLast(count).ToList();
    }

    private async Task PersistBreachesAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_breaches.ToList());
            await _storage.WriteTextAsync(BreachesFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در ذخیره‌ی نقض آستانه");
        }
    }
}