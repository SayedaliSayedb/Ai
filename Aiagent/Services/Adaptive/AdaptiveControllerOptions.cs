namespace Aiagent.Services.Adaptive;

/// <summary>
/// Configuration for the application-layer adaptive concurrency controller.
/// Values are starting points and must be tuned for the selected model/GPU.
/// </summary>
public sealed class AdaptiveControllerOptions
{
    public const string SectionName = "AdaptiveController";

    public bool Enabled { get; set; } = true;
    public string InitialMode { get; set; } = "ObserveOnly";

    public int MinConcurrency { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 8;
    public int InitialConcurrency { get; set; } = 2;

    public int ControlIntervalSeconds { get; set; } = 10;
    public int MetricsWindowSeconds { get; set; } = 120;
    public int ArrivalRateWindowSeconds { get; set; } = 60;
    public int MinSamplesForDecision { get; set; } = 10;
    public int MinSamplesForModelUpdate { get; set; } = 20;
    public int MinModelUpdates { get; set; } = 8;

    public double TargetP95TtftMs { get; set; } = 3000;
    public double TargetP95TpotMs { get; set; } = 150;
    public double TtftDeadbandRatio { get; set; } = 0.10;
    public double TpotDeadbandRatio { get; set; } = 0.08;

    public int QueueHighWatermark { get; set; } = 4;
    public int QueueLowWatermark { get; set; } = 1;
    public int CooldownSeconds { get; set; } = 20;
    public int HealthyWindowsBeforeIncrease { get; set; } = 2;

    public double GpuMemorySoftLimitRatio { get; set; } = 0.92;
    public double GpuMemoryEmergencyRatio { get; set; } = 0.97;
    public int GpuIndex { get; set; } = 0;
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    public double RlsForgettingFactor { get; set; } = 0.99;
    public double RlsInitialCovariance { get; set; } = 1000;

    public int MaxObservations { get; set; } = 10000;

    public bool IsValid(out string error)
    {
        if (MinConcurrency < 1) { error = "MinConcurrency must be >= 1."; return false; }
        if (MaxConcurrency < MinConcurrency) { error = "MaxConcurrency must be >= MinConcurrency."; return false; }
        if (InitialConcurrency < MinConcurrency || InitialConcurrency > MaxConcurrency)
        { error = "InitialConcurrency must be within MinConcurrency..MaxConcurrency."; return false; }
        if (ControlIntervalSeconds < 1 || MetricsWindowSeconds < ControlIntervalSeconds || ArrivalRateWindowSeconds < 1)
        { error = "Control/window intervals are invalid."; return false; }
        if (MinSamplesForDecision < 1 || MinSamplesForModelUpdate < 2 || MinModelUpdates < 2)
        { error = "Sample thresholds are invalid."; return false; }
        if (TargetP95TtftMs <= 0 || TargetP95TpotMs <= 0)
        { error = "SLO targets must be > 0."; return false; }
        if (RlsForgettingFactor <= 0 || RlsForgettingFactor > 1)
        { error = "RlsForgettingFactor must be in (0, 1]."; return false; }
        if (GpuMemorySoftLimitRatio <= 0 || GpuMemorySoftLimitRatio >= 1 ||
            GpuMemoryEmergencyRatio <= GpuMemorySoftLimitRatio || GpuMemoryEmergencyRatio > 1)
        { error = "GPU memory ratios must satisfy 0 < soft < emergency <= 1."; return false; }
        if (!Enum.TryParse<AdaptiveControllerMode>(InitialMode, true, out var initialMode) ||
            initialMode == AdaptiveControllerMode.Emergency)
        { error = "InitialMode must be Disabled, ObserveOnly, Fallback, or Adaptive. Emergency is entered automatically."; return false; }

        error = string.Empty;
        return true;
    }
}
