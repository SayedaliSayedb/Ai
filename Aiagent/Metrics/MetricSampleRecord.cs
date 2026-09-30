namespace Aiagent.Services.Metrics;

/// <summary>
/// DTO مخصوص ذخیره‌سازی در Parquet.
/// Parquet.Net از DateTimeOffset پشتیبانی نمی‌کند،
/// بنابراین از DateTime (UTC) استفاده می‌کنیم.
/// </summary>
public sealed class MetricSampleRecord
{
    public DateTime Timestamp { get; set; }

    public int ActiveConversations { get; set; }
    public int ActiveSequences { get; set; }
    public int QueuedRequests { get; set; }

    public double TtftP50 { get; set; }
    public double TtftP95 { get; set; }
    public double TtftP99 { get; set; }
    public double TpotP50 { get; set; }
    public double TpotP95 { get; set; }
    public double TpotP99 { get; set; }

    public double TokensPerSecond { get; set; }
    public double BatchesPerSecond { get; set; }

    public int NoKvSlotCount { get; set; }

    public double GpuUtilizationPercent { get; set; }
    public double GpuMemoryUsedMb { get; set; }
    public double GpuMemoryTotalMb { get; set; }
    public double KvCacheUsagePercent { get; set; }

    public bool SloViolated { get; set; }
}