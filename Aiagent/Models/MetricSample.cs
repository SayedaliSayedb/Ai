namespace Aiagent.Models;

/// <summary>
/// یک نمونه‌ی متریک که هر ثانیه ثبت می‌شود. این ساختار
/// مستقیماً به Parquet تبدیل می‌شود برای تحلیل‌های آفلاین.
/// </summary>
public sealed class MetricSample
{
    public DateTimeOffset Timestamp { get; set; }

    // === شاخص‌های اصلی پایان‌نامه ===
    public int ActiveConversations { get; set; }
    public int QueuedRequests { get; set; }
    public int ActiveSequences { get; set; }
    public int MaxSequences { get; set; }          // SeqMax

    // === تأخیر (میلی‌ثانیه) ===
    public double TtftP50 { get; set; }
    public double TtftP95 { get; set; }
    public double TtftP99 { get; set; }
    public double TpotP50 { get; set; }
    public double TpotP95 { get; set; }
    public double TpotP99 { get; set; }

    // === توان عملیاتی ===
    public double TokensPerSecond { get; set; }
    public double BatchesPerSecond { get; set; }

    // === خطا ===
    public int NoKvSlotCount { get; set; }
    public int TimeoutCount { get; set; }
    public int FailedRequests { get; set; }

    // === منابع ===
    public double GpuUtilizationPercent { get; set; }
    public double GpuMemoryUsedMb { get; set; }
    public double GpuMemoryTotalMb { get; set; }
    public double GpuTemperature { get; set; }
    public double CpuUtilizationPercent { get; set; }
    public double ProcessRamMb { get; set; }

    // === مشتق‌شده ===
    public double KvCacheUsagePercent { get; set; }
    public bool SloViolated { get; set; }
}

public sealed class ChatLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public required string SessionId { get; set; }
    public required string PhoneNumber { get; set; }
    public required string UserMessage { get; set; }
    public required string BotResponse { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double TtftMs { get; set; }
    public double TpotMs { get; set; }
    public double TotalMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}