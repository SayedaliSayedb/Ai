using Aiagent.Models;
using Aiagent.Services.Storage;
using Parquet;
using Parquet.Serialization;   // ← این خط اضافه شود
using Parquet.Schema;          // در صورت نیاز به DataField

namespace Aiagent.Services.Metrics;

public class MetricsPersistenceService : BackgroundService
{
    private readonly IMetricsCollector _collector;
    private readonly IStorageService _storage;
    private readonly ILogger<MetricsPersistenceService> _logger;

    private readonly List<MetricSample> _buffer = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);
    private const int MaxBufferSize = 720;

    public MetricsPersistenceService(
        IMetricsCollector collector,
        IStorageService storage,
        ILogger<MetricsPersistenceService> logger)
    {
        _collector = collector;
        _storage = storage;
        _logger = logger;
        _collector.OnSample += OnSample;
    }

    private void OnSample(MetricSample sample)
    {
        _ = Task.Run(async () =>
        {
            await _lock.WaitAsync();
            try
            {
                _buffer.Add(sample);
                if (_buffer.Count >= MaxBufferSize)
                    await FlushInternalAsync();
            }
            finally { _lock.Release(); }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "خطا در ذخیره‌سازی متریک");
            }
        }

        await FlushAsync(CancellationToken.None);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { await FlushInternalAsync(); }
        finally { _lock.Release(); }
    }

    private async Task FlushInternalAsync()
    {
        if (_buffer.Count == 0) return;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd/HH");
        var fileName = $"metrics/{timestamp}/{Guid.NewGuid():N}.parquet";

        try
        {
            var records = _buffer.Select(s => new MetricSampleRecord
            {
                // ... (تبدیل‌ها دست‌نخورده باقی می‌ماند)
            }).ToList();

            using var ms = new MemoryStream();

            // ✅ تغییر کلیدی: استفاده از ParquetOptions به جای ParquetSerializerOptions
            await ParquetSerializer.SerializeAsync(
                records, ms,
                new ParquetOptions
                {
                    CompressionMethod = CompressionMethod.Gzip
                });

            await _storage.WriteBytesAsync(fileName, ms.ToArray());

            _logger.LogInformation(
                "✅ {Count} نمونه‌ی متریک در {File} ذخیره شد ({Size} KB)",
                records.Count, fileName, ms.Length / 1024);

            _buffer.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در نوشتن Parquet");
            _buffer.Clear();
        }
    }
}