namespace Aiagent.Services.Metrics;

public interface IGpuMonitor
{
    GpuMetrics? Read();
    bool IsAvailable { get; }
}

public sealed class GpuMetrics
{
    public double UtilizationPercent { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double Temperature { get; init; }
}