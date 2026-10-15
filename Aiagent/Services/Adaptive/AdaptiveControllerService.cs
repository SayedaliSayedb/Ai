using Microsoft.Extensions.Options;

namespace Aiagent.Services.Adaptive;

/// <summary>
/// Periodically measures the service, updates window-level RLS estimators, and changes
/// only the application's admission concurrency target. It never changes model parameters.
/// </summary>
public sealed class AdaptiveControllerService : BackgroundService
{
    private const int FeatureCount = 6;
    private readonly AdaptiveTelemetryStore _telemetry;
    private readonly AdaptiveControllerState _state;
    private readonly IGpuTelemetryProvider _gpuProvider;
    private readonly AdaptiveControllerOptions _options;
    private readonly ILogger<AdaptiveControllerService> _logger;
    private readonly RlsEstimator _ttftEstimator;
    private readonly RlsEstimator _tpotEstimator;
    private readonly HashSet<int> _trainedConcurrencyLevels = new();
    private readonly Dictionary<int, long> _lastTrainingSequencesByConcurrency = new();
    private long _lastDecisionSequence;
    private DateTimeOffset _lastControlChangeUtc = DateTimeOffset.MinValue;
    private int _healthyWindows;
    private int _emergencyRecoveryWindows;
    private AdaptiveControllerMode _modeBeforeEmergency = AdaptiveControllerMode.Fallback;

    public AdaptiveControllerService(
        AdaptiveTelemetryStore telemetry,
        AdaptiveControllerState state,
        IGpuTelemetryProvider gpuProvider,
        IOptions<AdaptiveControllerOptions> options,
        ILogger<AdaptiveControllerService> logger)
    {
        _telemetry = telemetry;
        _state = state;
        _gpuProvider = gpuProvider;
        _options = options.Value;
        _logger = logger;
        _ttftEstimator = new RlsEstimator(FeatureCount, _options.RlsForgettingFactor, _options.RlsInitialCovariance);
        _tpotEstimator = new RlsEstimator(FeatureCount, _options.RlsForgettingFactor, _options.RlsInitialCovariance);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsValid(out var validationError))
        {
            _logger.LogError("Adaptive controller configuration invalid: {Error}; automatic control disabled.", validationError);
            _state.TrySetMode("ObserveOnly", out _);
        }
        else
        {
            _logger.LogInformation(
                "Adaptive controller started. Enabled={Enabled}, Mode={Mode}, Concurrency=[{Min},{Max}], target={Target}",
                _options.Enabled, _state.Mode, _state.MinConcurrency, _state.MaxConcurrency, _state.TargetConcurrency);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ControlIntervalSeconds)), stoppingToken);
                await ControlStepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adaptive controller step failed; leaving last concurrency target unchanged.");
            }
        }
    }

    private async Task ControlStepAsync(CancellationToken cancellationToken)
    {
        var gpu = await _gpuProvider.GetSnapshotAsync(cancellationToken);
        var currentTarget = _state.TargetConcurrency;
        var snapshot = _telemetry.GetSnapshot(
            _options.MetricsWindowSeconds,
            _options.ArrivalRateWindowSeconds,
            currentTarget);

        UpdateEstimators(snapshot);

        var mode = _state.Mode;
        var decision = "observe-only: no control action";

        if (!_options.Enabled || mode == AdaptiveControllerMode.Disabled)
        {
            decision = "controller disabled";
        }
        else if (mode == AdaptiveControllerMode.ObserveOnly)
        {
            decision = "observe-only: collected metrics, target unchanged";
        }
        else
        {
            var emergency = gpu.Available && gpu.MemoryUsedRatio.HasValue &&
                            gpu.MemoryUsedRatio.Value >= _options.GpuMemoryEmergencyRatio;
            if (emergency && mode != AdaptiveControllerMode.Emergency)
            {
                _modeBeforeEmergency = mode;
                _state.TrySetMode("Emergency", out _);
                _emergencyRecoveryWindows = 0;
                var emergencyTarget = _state.SetTargetConcurrency(_state.MinConcurrency,
                    $"EMERGENCY: GPU memory {gpu.MemoryUsedRatio:P1}; target reduced to minimum");
                _lastControlChangeUtc = DateTimeOffset.UtcNow;
                decision = $"emergency memory guard -> {emergencyTarget}";
                _logger.LogWarning("Adaptive controller entered EMERGENCY. GPU memory={Ratio:P1}.", gpu.MemoryUsedRatio);
            }
            else if (mode == AdaptiveControllerMode.Emergency)
            {
                var recovered = gpu.Available && gpu.MemoryUsedRatio.HasValue &&
                                gpu.MemoryUsedRatio.Value < _options.GpuMemorySoftLimitRatio;
                _emergencyRecoveryWindows = recovered ? _emergencyRecoveryWindows + 1 : 0;
                if (_emergencyRecoveryWindows >= 3)
                {
                    _state.TrySetMode(_modeBeforeEmergency.ToString(), out _);
                    _emergencyRecoveryWindows = 0;
                    decision = "GPU memory recovered; restored pre-emergency mode";
                }
                else
                {
                    _state.SetTargetConcurrency(_state.MinConcurrency, "emergency hold: waiting for GPU memory recovery");
                    decision = "emergency hold";
                }
            }
            else if (snapshot.LatestSequence <= _lastDecisionSequence)
            {
                decision = "no new completed requests; target unchanged";
            }
            else if (snapshot.TrainingSampleCount < _options.MinSamplesForDecision)
            {
                decision = $"insufficient observations at current concurrency ({snapshot.TrainingSampleCount}/{_options.MinSamplesForDecision}); target unchanged";
            }
            else if (DateTimeOffset.UtcNow - _lastControlChangeUtc < TimeSpan.FromSeconds(_options.CooldownSeconds))
            {
                decision = "cooldown: target unchanged";
            }
            else
            {
                var decisionSnapshot = snapshot with
                {
                    CompletedSamplesInWindow = snapshot.TrainingSampleCount,
                    P95TtftMs = snapshot.TrainingP95TtftMs,
                    P95TpotMs = snapshot.TrainingP95TpotMs
                };
                var candidate = mode == AdaptiveControllerMode.Adaptive && IsModelReady()
                    ? ChooseModelBasedTarget(snapshot, gpu)
                    : null;

                if (candidate.HasValue)
                {
                    decision = ApplyTarget(candidate.Value, "RLS prediction");
                }
                else
                {
                    var fallbackTarget = ChooseFallbackTarget(decisionSnapshot, gpu, currentTarget);
                    decision = ApplyTarget(fallbackTarget, mode == AdaptiveControllerMode.Adaptive
                        ? "RLS not ready/unsafe; threshold fallback" : "threshold fallback");
                }
            }
        }

        _lastDecisionSequence = Math.Max(_lastDecisionSequence, snapshot.LatestSequence);
        var modelReady = IsModelReady();
        _state.SetDiagnostics(new AdaptiveControllerDiagnostics
        {
            LastDecision = decision,
            CompletedSamplesInWindow = snapshot.CompletedSamplesInWindow,
            P95TtftMs = snapshot.P95TtftMs,
            P95InferenceTtftMs = snapshot.P95InferenceTtftMs,
            P95QueueWaitMs = snapshot.P95QueueWaitMs,
            P95TpotMs = snapshot.P95TpotMs,
            P99TtftMs = snapshot.P99TtftMs,
            P99TpotMs = snapshot.P99TpotMs,
            ArrivalRatePerSecond = snapshot.ArrivalRatePerSecond,
            ActiveSequences = snapshot.ActiveSequences,
            QueueDepth = snapshot.QueueDepth,
            GpuMemoryUsedRatio = gpu.Available ? gpu.MemoryUsedRatio : null,
            GpuUtilizationPercent = gpu.Available ? gpu.UtilizationPercent : null,
            TtftModelUpdates = (int)Math.Min(int.MaxValue, _ttftEstimator.UpdateCount),
            TpotModelUpdates = (int)Math.Min(int.MaxValue, _tpotEstimator.UpdateCount),
            TtftModelResidualEwmaMs = _ttftEstimator.UpdateCount > 0 ? _ttftEstimator.ResidualEwma : null,
            TpotModelResidualEwmaMs = _tpotEstimator.UpdateCount > 0 ? _tpotEstimator.ResidualEwma : null,
            ModelReady = modelReady,
            GpuTelemetryAvailable = gpu.Available
        });

        _logger.LogInformation(
            "Adaptive[{Mode}] target={Target}, active={Active}, queue={Queue}, n={N}, p95TTFT={Ttft}, p95TPOT={Tpot}, arrival/s={Arrival:F2}, GPUmem={Gpu}, decision={Decision}",
            _state.Mode, _state.TargetConcurrency, snapshot.ActiveSequences, snapshot.QueueDepth,
            snapshot.CompletedSamplesInWindow,
            snapshot.P95TtftMs?.ToString("F0") ?? "n/a",
            snapshot.P95TpotMs?.ToString("F1") ?? "n/a",
            snapshot.ArrivalRatePerSecond,
            gpu.Available && gpu.MemoryUsedRatio.HasValue ? gpu.MemoryUsedRatio.Value.ToString("P1") : "n/a",
            decision);
    }

    private void UpdateEstimators(AdaptiveWindowSnapshot s)
    {
        var lastSequenceForConcurrency = _lastTrainingSequencesByConcurrency.TryGetValue(s.TrainingConcurrency, out var seen) ? seen : 0;
        if (s.TrainingSampleCount < _options.MinSamplesForModelUpdate ||
            s.TrainingLatestSequence <= lastSequenceForConcurrency ||
            !s.TrainingP95TtftMs.HasValue || !s.TrainingP95TpotMs.HasValue)
            return;

        var features = BuildFeatures(
            s.TrainingConcurrency,
            s.TrainingAverageArrivalRatePerSecond,
            s.TrainingAverageQueueDepth,
            s.TrainingAveragePromptTokens,
            s.TrainingAverageOutputTokens);

        var ttftUpdated = _ttftEstimator.Update(features, s.TrainingP95TtftMs.Value, s.TrainingConcurrency);
        var tpotUpdated = _tpotEstimator.Update(features, s.TrainingP95TpotMs.Value, s.TrainingConcurrency);
        if (ttftUpdated && tpotUpdated)
        {
            _lastTrainingSequencesByConcurrency[s.TrainingConcurrency] = s.TrainingLatestSequence;
            _trainedConcurrencyLevels.Add(s.TrainingConcurrency);
        }
    }

    private int? ChooseModelBasedTarget(AdaptiveWindowSnapshot s, GpuTelemetrySnapshot gpu)
    {
        if (!IsModelReady()) return null;
        if (gpu.Available && gpu.MemoryUsedRatio.HasValue &&
            gpu.MemoryUsedRatio.Value >= _options.GpuMemorySoftLimitRatio)
        {
            // Under memory pressure, always step down (or hold at minimum); never explore upward.
            return Math.Max(_state.MinConcurrency, _state.TargetConcurrency - 1);
        }

        var minObserved = Math.Max(_state.MinConcurrency,
            Math.Max(_ttftEstimator.MinObservedConcurrency, _tpotEstimator.MinObservedConcurrency));
        var maxObserved = Math.Min(_state.MaxConcurrency,
            Math.Min(_ttftEstimator.MaxObservedConcurrency, _tpotEstimator.MaxObservedConcurrency));
        if (minObserved > maxObserved) return null;

        int? best = null;
        var bestScore = double.PositiveInfinity;
        for (var k = minObserved; k <= maxObserved; k++)
        {
            var x = BuildFeatures(k, s.ArrivalRatePerSecond, s.QueueDepth,
                s.AveragePromptTokens, s.AverageOutputTokens);
            if (!_ttftEstimator.TryPredict(x, out var predictedTtft) ||
                !_tpotEstimator.TryPredict(x, out var predictedTpot))
                continue;

            var ttftLimit = _options.TargetP95TtftMs * (1 + _options.TtftDeadbandRatio);
            var tpotLimit = _options.TargetP95TpotMs * (1 + _options.TpotDeadbandRatio);
            if (predictedTtft > ttftLimit || predictedTpot > tpotLimit) continue;
            if (gpu.Available && gpu.MemoryUsedRatio >= _options.GpuMemorySoftLimitRatio && k > _state.TargetConcurrency)
                continue;

            var score = predictedTtft / _options.TargetP95TtftMs + predictedTpot / _options.TargetP95TpotMs;
            // A small switching penalty helps avoid oscillation between equally-scored candidates.
            score += Math.Abs(k - _state.TargetConcurrency) * 0.03;
            if (score < bestScore)
            {
                bestScore = score;
                best = k;
            }
        }

        if (!best.HasValue) return ChooseFallbackTarget(s, gpu, _state.TargetConcurrency);
        var boundedStep = Math.Clamp(best.Value,
            _state.TargetConcurrency - 1, _state.TargetConcurrency + 1);
        return Math.Clamp(boundedStep, _state.MinConcurrency, _state.MaxConcurrency);
    }

    private int ChooseFallbackTarget(AdaptiveWindowSnapshot s, GpuTelemetrySnapshot gpu, int current)
    {
        var next = current;
        if (gpu.Available && gpu.MemoryUsedRatio.HasValue &&
            gpu.MemoryUsedRatio.Value >= _options.GpuMemorySoftLimitRatio)
        {
            _healthyWindows = 0;
            return Math.Max(_state.MinConcurrency, current - 1);
        }

        var ttftHigh = s.P95TtftMs.HasValue &&
                       s.P95TtftMs.Value > _options.TargetP95TtftMs * (1 + _options.TtftDeadbandRatio);
        var tpotHigh = s.P95TpotMs.HasValue &&
                       s.P95TpotMs.Value > _options.TargetP95TpotMs * (1 + _options.TpotDeadbandRatio);
        var tpotComfortable = s.P95TpotMs.HasValue &&
                              s.P95TpotMs.Value < _options.TargetP95TpotMs * (1 - _options.TpotDeadbandRatio);

        if (tpotHigh)
        {
            _healthyWindows = 0;
            next = Math.Max(_state.MinConcurrency, current - 1);
        }
        else if ((ttftHigh || s.QueueDepth >= _options.QueueHighWatermark) && tpotComfortable)
        {
            _healthyWindows++;
            if (_healthyWindows >= Math.Max(1, _options.HealthyWindowsBeforeIncrease))
            {
                next = Math.Min(_state.MaxConcurrency, current + 1);
                _healthyWindows = 0;
            }
        }
        else if (s.QueueDepth <= _options.QueueLowWatermark && !ttftHigh && !tpotHigh)
        {
            _healthyWindows = 0;
        }
        else
        {
            _healthyWindows = 0;
        }

        return Math.Clamp(next, _state.MinConcurrency, _state.MaxConcurrency);
    }

    private string ApplyTarget(int candidate, string source)
    {
        var current = _state.TargetConcurrency;
        var target = Math.Clamp(candidate, Math.Max(_state.MinConcurrency, current - 1),
            Math.Min(_state.MaxConcurrency, current + 1));
        if (target == current) return $"{source}: hold {current}";
        _state.SetTargetConcurrency(target, $"{source}: {current} -> {target}");
        _lastControlChangeUtc = DateTimeOffset.UtcNow;
        _logger.LogWarning("Adaptive controller changed target concurrency {Old} -> {New} ({Source}).", current, target, source);
        return $"{source}: {current} -> {target}";
    }

    private bool IsModelReady() =>
        _ttftEstimator.UpdateCount >= _options.MinModelUpdates &&
        _tpotEstimator.UpdateCount >= _options.MinModelUpdates &&
        _trainedConcurrencyLevels.Count >= 2 &&
        _ttftEstimator.MinObservedConcurrency < _ttftEstimator.MaxObservedConcurrency &&
        _tpotEstimator.MinObservedConcurrency < _tpotEstimator.MaxObservedConcurrency;

    private static double[] BuildFeatures(
        int concurrency,
        double arrivalRate,
        double queueDepth,
        double averagePromptTokens,
        double averageOutputTokens) => new[]
        {
            1.0,
            (double)concurrency,
            Math.Max(0, arrivalRate) / 5.0,
            Math.Max(0, queueDepth) / 10.0,
            Math.Max(0, averagePromptTokens) / 512.0,
            Math.Max(0, averageOutputTokens) / 512.0
        };
}
