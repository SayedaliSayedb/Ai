using System.Text.Json.Serialization;

namespace Aiagent.Services.LoadTesting;

// ============================================================
// درخواست شروع آزمون
// ============================================================
public sealed class LoadTestRequest
{
    public int Concurrency { get; set; } = 4;
    public int RequestsPerUser { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 120;

    public bool EnableScheduling { get; set; }
    public bool EnableKvCachePolicy { get; set; }
    public bool EnableChunkedPrefill { get; set; }

    public string VariantName { get; set; } = "custom";

    public List<string> Prompts { get; set; } = new();

    public string? SystemPrompt { get; set; }
}

// ============================================================
// یک اجرا
// ============================================================
public sealed class LoadTestRun
{
    public required string RunId { get; init; }
    public required LoadTestRequest Request { get; init; }
    public required int TotalRequests { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public LoadTestStatus Status { get; set; } = LoadTestStatus.Running;
    public string? Error { get; set; }

    public int CompletedRequests;
    public List<LoadTestResult> Results { get; } = new();
}

public enum LoadTestStatus { Running, Completed, Failed }

// ============================================================
// نتیجه‌ی یک درخواست
// ============================================================
public sealed class LoadTestResult
{
    public int UserIndex { get; init; }
    public int RequestIndex { get; init; }
    public required string Prompt { get; init; }
    public DateTimeOffset SentAt { get; set; }

    // ⬅️ [FIX] این‌ها باید اینجا باشند، نه در Summary
    [JsonPropertyName("ttftMs")]
    public double TtftMs { get; set; }

    [JsonPropertyName("tpotMs")]
    public double TpotMs { get; set; }

    [JsonPropertyName("e2eMs")]
    public double E2EMs { get; set; }

    public int ChunkCount { get; set; }

    public bool Success { get; set; }
    public string? ErrorKind { get; set; }
    public string? ErrorMessage { get; set; }
}

// ============================================================
// خلاصه‌ی آماری
// ============================================================
public sealed class LoadTestSummary
{
    public required string RunId { get; init; }
    public required string VariantName { get; init; }
    public int Concurrency { get; init; }
    public int TotalRequests { get; init; }
    public int Successful { get; init; }
    public int Failed { get; init; }
    public double DurationSeconds { get; init; }
    public double ThroughputRps { get; init; }
    public double ThroughputTps { get; init; }

    // ⬅️ [FIX] این‌ها Percentiles هستند و نام‌گذاری JSON‌شان صریح است
    [JsonPropertyName("ttft")]
    public Percentiles Ttft { get; init; }

    [JsonPropertyName("tpot")]
    public Percentiles Tpot { get; init; }

    [JsonPropertyName("e2e")]
    public Percentiles E2E { get; init; }

    public Dictionary<string, int> ErrorsByKind { get; init; } = new();
}

public readonly record struct Percentiles(
    double P50, double P95, double P99, double Mean, double Max);