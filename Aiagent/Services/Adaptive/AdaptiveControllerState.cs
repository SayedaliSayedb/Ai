using Microsoft.Extensions.Options;

namespace Aiagent.Services.Adaptive;

public sealed class AdaptiveControllerState
{
    private readonly object _gate = new();
    private readonly AdaptiveControllerOptions _options;
    private int _targetConcurrency;
    private int _effectiveMaximumConcurrency;
    private int _effectiveMinimumConcurrency;
    private AdaptiveControllerMode _mode;
    private AdaptiveControllerStatus _status;

    public AdaptiveControllerState(IOptions<AdaptiveControllerOptions> options)
    {
        _options = options.Value;
        _effectiveMaximumConcurrency = _options.MaxConcurrency;
        _effectiveMinimumConcurrency = Math.Min(_options.MinConcurrency, _effectiveMaximumConcurrency);
        _targetConcurrency = Math.Clamp(_options.InitialConcurrency,
            _effectiveMinimumConcurrency, _effectiveMaximumConcurrency);
        _mode = Enum.TryParse<AdaptiveControllerMode>(_options.InitialMode, true, out var parsed)
            ? parsed : AdaptiveControllerMode.ObserveOnly;
        _status = new AdaptiveControllerStatus
        {
            Mode = _mode.ToString(),
            TargetConcurrency = _targetConcurrency,
            MinConcurrency = _options.MinConcurrency,
            MaxConcurrency = _options.MaxConcurrency,
            LastDecision = "startup",
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    public bool Enabled => _options.Enabled;
    public int MinConcurrency { get { lock (_gate) return _effectiveMinimumConcurrency; } }
    public int MaxConcurrency { get { lock (_gate) return _effectiveMaximumConcurrency; } }

    /// <summary>Clamp controller targets to the real engine/scheduler capacity discovered by the host.</summary>
    public void ConfigureRuntimeMaximum(int maximum)
    {
        lock (_gate)
        {
            _effectiveMaximumConcurrency = Math.Clamp(maximum, 1, _options.MaxConcurrency);
            _effectiveMinimumConcurrency = Math.Min(_options.MinConcurrency, _effectiveMaximumConcurrency);
            _targetConcurrency = Math.Clamp(_targetConcurrency, _effectiveMinimumConcurrency, _effectiveMaximumConcurrency);
            _status = _status with
            {
                MinConcurrency = _effectiveMinimumConcurrency,
                MaxConcurrency = _effectiveMaximumConcurrency,
                TargetConcurrency = _targetConcurrency,
                LastDecision = "runtime capacity applied",
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public int TargetConcurrency
    {
        get { lock (_gate) return _targetConcurrency; }
    }

    public AdaptiveControllerMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    public bool IsActuating
    {
        get
        {
            lock (_gate)
                return _options.Enabled && (_mode is AdaptiveControllerMode.Fallback
                    or AdaptiveControllerMode.Adaptive or AdaptiveControllerMode.Emergency);
        }
    }

    public bool TrySetMode(string? mode, out string error)
    {
        if (!Enum.TryParse<AdaptiveControllerMode>(mode, true, out var parsed))
        {
            error = "Mode must be Disabled, ObserveOnly, Fallback, or Adaptive.";
            return false;
        }
        if (parsed == AdaptiveControllerMode.Emergency)
        {
            error = "Emergency mode is controlled automatically by the safety policy.";
            return false;
        }
        lock (_gate)
        {
            _mode = parsed;
            if (parsed == AdaptiveControllerMode.Disabled || parsed == AdaptiveControllerMode.ObserveOnly)
            {
                // Do not rewrite the target; these modes simply stop automatic actuation.
            }
            _status = _status with
            {
                Mode = _mode.ToString(),
                TargetConcurrency = _targetConcurrency,
                LastDecision = $"mode changed to {_mode}",
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
        }
        error = string.Empty;
        return true;
    }

    public int SetTargetConcurrency(int target, string decision)
    {
        lock (_gate)
        {
            var clamped = Math.Clamp(target, _effectiveMinimumConcurrency, _effectiveMaximumConcurrency);
            if (_mode == AdaptiveControllerMode.Emergency)
                clamped = _effectiveMinimumConcurrency;
            _targetConcurrency = clamped;
            _status = _status with
            {
                Mode = _mode.ToString(),
                TargetConcurrency = _targetConcurrency,
                LastDecision = decision,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
            return _targetConcurrency;
        }
    }

    public void SetDiagnostics(AdaptiveControllerDiagnostics diagnostics)
    {
        lock (_gate)
        {
            _status = new AdaptiveControllerStatus
            {
                Mode = _mode.ToString(),
                TargetConcurrency = _targetConcurrency,
                MinConcurrency = _effectiveMinimumConcurrency,
                MaxConcurrency = _effectiveMaximumConcurrency,
                LastDecision = diagnostics.LastDecision ?? _status.LastDecision,
                LastUpdatedUtc = DateTimeOffset.UtcNow,
                CompletedSamplesInWindow = diagnostics.CompletedSamplesInWindow,
                P95TtftMs = diagnostics.P95TtftMs,
                P95InferenceTtftMs = diagnostics.P95InferenceTtftMs,
                P95QueueWaitMs = diagnostics.P95QueueWaitMs,
                P95TpotMs = diagnostics.P95TpotMs,
                P99TtftMs = diagnostics.P99TtftMs,
                P99TpotMs = diagnostics.P99TpotMs,
                ArrivalRatePerSecond = diagnostics.ArrivalRatePerSecond,
                ActiveSequences = diagnostics.ActiveSequences,
                QueueDepth = diagnostics.QueueDepth,
                GpuMemoryUsedRatio = diagnostics.GpuMemoryUsedRatio,
                GpuUtilizationPercent = diagnostics.GpuUtilizationPercent,
                TtftModelUpdates = diagnostics.TtftModelUpdates,
                TpotModelUpdates = diagnostics.TpotModelUpdates,
                TtftModelResidualEwmaMs = diagnostics.TtftModelResidualEwmaMs,
                TpotModelResidualEwmaMs = diagnostics.TpotModelResidualEwmaMs,
                ModelReady = diagnostics.ModelReady,
                GpuTelemetryAvailable = diagnostics.GpuTelemetryAvailable
            };
        }
    }

    public AdaptiveControllerStatus GetStatus()
    {
        lock (_gate) return _status;
    }
}

public sealed record AdaptiveControllerDiagnostics
{
    public string? LastDecision { get; init; }
    public int CompletedSamplesInWindow { get; init; }
    public double? P95TtftMs { get; init; }
    public double? P95InferenceTtftMs { get; init; }
    public double? P95QueueWaitMs { get; init; }
    public double? P95TpotMs { get; init; }
    public double? P99TtftMs { get; init; }
    public double? P99TpotMs { get; init; }
    public double ArrivalRatePerSecond { get; init; }
    public int ActiveSequences { get; init; }
    public int QueueDepth { get; init; }
    public double? GpuMemoryUsedRatio { get; init; }
    public double? GpuUtilizationPercent { get; init; }
    public int TtftModelUpdates { get; init; }
    public int TpotModelUpdates { get; init; }
    public double? TtftModelResidualEwmaMs { get; init; }
    public double? TpotModelResidualEwmaMs { get; init; }
    public bool ModelReady { get; init; }
    public bool GpuTelemetryAvailable { get; init; }
}

public sealed record AdaptiveControllerStatus
{
    public string Mode { get; init; } = "ObserveOnly";
    public int TargetConcurrency { get; init; }
    public int MinConcurrency { get; init; }
    public int MaxConcurrency { get; init; }
    public string LastDecision { get; init; } = "not started";
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public int CompletedSamplesInWindow { get; init; }
    public double? P95TtftMs { get; init; }
    public double? P95InferenceTtftMs { get; init; }
    public double? P95QueueWaitMs { get; init; }
    public double? P95TpotMs { get; init; }
    public double? P99TtftMs { get; init; }
    public double? P99TpotMs { get; init; }
    public double ArrivalRatePerSecond { get; init; }
    public int ActiveSequences { get; init; }
    public int QueueDepth { get; init; }
    public double? GpuMemoryUsedRatio { get; init; }
    public double? GpuUtilizationPercent { get; init; }
    public int TtftModelUpdates { get; init; }
    public int TpotModelUpdates { get; init; }
    public double? TtftModelResidualEwmaMs { get; init; }
    public double? TpotModelResidualEwmaMs { get; init; }
    public bool ModelReady { get; init; }
    public bool GpuTelemetryAvailable { get; init; }
}
