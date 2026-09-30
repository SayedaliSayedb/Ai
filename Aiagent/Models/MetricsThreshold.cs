namespace Aiagent.Models;

public sealed class MetricsThreshold
{
    public double TtftP95Max { get; set; } = 2000;      // ms
    public double TpotP95Max { get; set; } = 500;        // ms
    public double KvCacheMax { get; set; } = 90;         // %
    public double GpuMemoryMax { get; set; } = 90;       // %
    public double GpuUtilizationMax { get; set; } = 95;  // %
    public double ErrorRateMax { get; set; } = 1;        // %
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// یک رکورد نقض آستانه - برای ذخیره‌سازی در Parquet
/// </summary>
public sealed class ThresholdBreachRecord
{
    public DateTime Timestamp { get; set; }
    public string MetricName { get; set; } = "";     // "TTFT_p95", "KV_Cache", ...
    public double Value { get; set; }
    public double Threshold { get; set; }
    public string Severity { get; set; } = "Warning"; // "Warning", "Critical"
    public int ActiveConversations { get; set; }
    public double GpuUtilization { get; set; }
}