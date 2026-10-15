namespace Aiagent.Services.Adaptive;

public sealed record GpuTelemetrySnapshot
{
    public bool Available { get; init; }
    public int GpuIndex { get; init; }
    public long? MemoryUsedMiB { get; init; }
    public long? MemoryTotalMiB { get; init; }
    public double? MemoryUsedRatio { get; init; }
    public double? UtilizationPercent { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public static GpuTelemetrySnapshot Unavailable(int index, string? error = null) => new()
    {
        Available = false,
        GpuIndex = index,
        Error = error,
        TimestampUtc = DateTimeOffset.UtcNow
    };
}

public interface IGpuTelemetryProvider
{
    Task<GpuTelemetrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
