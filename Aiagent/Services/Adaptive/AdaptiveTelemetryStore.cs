using System.Collections.Generic;

namespace Aiagent.Services.Adaptive;

/// <summary>
/// Bounded in-memory telemetry buffer used by the adaptive controller.
/// Stores only timings/counts; it never stores prompts or generated text.
/// </summary>
public sealed class AdaptiveTelemetryStore
{
    private readonly object _gate = new();
    private readonly Queue<CompletionObservation> _observations = new();
    private readonly Queue<DateTimeOffset> _arrivals = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly int _maxObservations;
    private readonly int _arrivalRateWindowSeconds;
    private long _sequence;
    private long _failureCount;
    private Func<int>? _activeCountProvider;
    private Func<int>? _queueCountProvider;
    private Func<int>? _maxSequencesProvider;

    public AdaptiveTelemetryStore(Microsoft.Extensions.Options.IOptions<AdaptiveControllerOptions> options)
    {
        _maxObservations = Math.Max(100, options.Value.MaxObservations);
        _arrivalRateWindowSeconds = Math.Max(1, options.Value.ArrivalRateWindowSeconds);
    }

    public void SetRuntimeProviders(Func<int> activeCount, Func<int> queueCount, Func<int> maxSequences)
    {
        _activeCountProvider = activeCount;
        _queueCountProvider = queueCount;
        _maxSequencesProvider = maxSequences;
    }

    public void RecordArrival(string requestId, int approximatePromptTokens)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _pending[requestId] = new PendingRequest(now, Math.Max(0, approximatePromptTokens));
            _arrivals.Enqueue(now);
            PruneArrivals(now - TimeSpan.FromMinutes(10));
        }
    }

    public void RecordAdmitted(string requestId, DateTimeOffset? admittedAt, int targetConcurrency)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        lock (_gate)
        {
            if (_pending.TryGetValue(requestId, out var pending))
            {
                pending.AdmittedAt = admittedAt ?? DateTimeOffset.UtcNow;
                pending.TargetConcurrency = Math.Max(1, targetConcurrency);
            }
        }
    }

    public void ForgetPending(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        lock (_gate) _pending.Remove(requestId);
    }

    public void RecordFailure(string requestId)
    {
        lock (_gate)
        {
            _pending.Remove(requestId);
            _failureCount++;
        }
    }

    public void RecordCompletion(
        string requestId,
        DateTime? firstTokenAt,
        DateTime? lastTokenAt,
        int generatedTokens,
        int activeSequences,
        int queueDepth,
        int targetConcurrency)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending))
            {
                // Do not invent request start time when the request was not tracked.
                return;
            }
            _pending.Remove(requestId);

            double? ttftMs = null;
            if (firstTokenAt.HasValue)
            {
                var first = new DateTimeOffset(DateTime.SpecifyKind(firstTokenAt.Value, DateTimeKind.Utc));
                var value = (first - pending.StartedAt).TotalMilliseconds;
                if (double.IsFinite(value) && value >= 0) ttftMs = value;
            }

            double? queueWaitMs = null;
            double? inferenceTtftMs = null;
            if (pending.AdmittedAt.HasValue)
            {
                var queueWait = (pending.AdmittedAt.Value - pending.StartedAt).TotalMilliseconds;
                if (double.IsFinite(queueWait) && queueWait >= 0) queueWaitMs = queueWait;
                if (firstTokenAt.HasValue)
                {
                    var first = new DateTimeOffset(DateTime.SpecifyKind(firstTokenAt.Value, DateTimeKind.Utc));
                    var inferenceTtft = (first - pending.AdmittedAt.Value).TotalMilliseconds;
                    if (double.IsFinite(inferenceTtft) && inferenceTtft >= 0) inferenceTtftMs = inferenceTtft;
                }
            }

            double? tpotMs = null;
            if (generatedTokens > 1 && firstTokenAt.HasValue && lastTokenAt.HasValue)
            {
                var intervalCount = generatedTokens - 1;
                var value = (lastTokenAt.Value - firstTokenAt.Value).TotalMilliseconds / intervalCount;
                if (double.IsFinite(value) && value >= 0) tpotMs = value;
            }

            var rateWindowStart = now - TimeSpan.FromSeconds(_arrivalRateWindowSeconds);
            PruneArrivals(rateWindowStart);
            var rate = _arrivals.Count / (double)_arrivalRateWindowSeconds;

            var observation = new CompletionObservation(
                Sequence: ++_sequence,
                TimestampUtc: now,
                TtftMs: ttftMs,
                InferenceTtftMs: inferenceTtftMs,
                QueueWaitMs: queueWaitMs,
                TpotMs: tpotMs,
                PromptTokens: pending.PromptTokens,
                OutputTokens: Math.Max(0, generatedTokens),
                ActiveSequences: Math.Max(0, activeSequences),
                QueueDepth: Math.Max(0, queueDepth),
                TargetConcurrency: pending.TargetConcurrency > 0 ? pending.TargetConcurrency : Math.Max(1, targetConcurrency),
                ArrivalRatePerSecond: rate);

            _observations.Enqueue(observation);
            while (_observations.Count > _maxObservations)
                _observations.Dequeue();
        }
    }

    public AdaptiveWindowSnapshot GetSnapshot(
        int metricsWindowSeconds,
        int arrivalRateWindowSeconds,
        int currentTargetConcurrency)
    {
        var now = DateTimeOffset.UtcNow;
        var metricsCutoff = now - TimeSpan.FromSeconds(Math.Max(1, metricsWindowSeconds));
        var arrivalCutoff = now - TimeSpan.FromSeconds(Math.Max(1, arrivalRateWindowSeconds));

        CompletionObservation[] all;
        CompletionObservation[] training;
        double arrivalRate;
        long failures;
        long latestSequence;
        lock (_gate)
        {
            while (_observations.Count > 0 && _observations.Peek().TimestampUtc < metricsCutoff)
                _observations.Dequeue();
            PruneArrivals(arrivalCutoff);
            all = _observations.Where(o => o.TimestampUtc >= metricsCutoff).ToArray();
            training = all.Where(o => o.TargetConcurrency == currentTargetConcurrency).ToArray();
            arrivalRate = _arrivals.Count / (double)Math.Max(1, arrivalRateWindowSeconds);
            failures = _failureCount;
            latestSequence = _sequence;
        }

        var active = SafeInvoke(_activeCountProvider);
        var queued = SafeInvoke(_queueCountProvider);
        var maxSequences = SafeInvoke(_maxSequencesProvider);

        return new AdaptiveWindowSnapshot
        {
            CreatedAtUtc = now,
            LatestSequence = latestSequence,
            CompletedSamplesInWindow = all.Length,
            ValidTtftSamples = all.Count(o => o.TtftMs.HasValue),
            ValidTpotSamples = all.Count(o => o.TpotMs.HasValue),
            P95TtftMs = Percentile(all.Where(o => o.TtftMs.HasValue).Select(o => o.TtftMs!.Value), 0.95),
            P95InferenceTtftMs = Percentile(all.Where(o => o.InferenceTtftMs.HasValue).Select(o => o.InferenceTtftMs!.Value), 0.95),
            P95QueueWaitMs = Percentile(all.Where(o => o.QueueWaitMs.HasValue).Select(o => o.QueueWaitMs!.Value), 0.95),
            P95TpotMs = Percentile(all.Where(o => o.TpotMs.HasValue).Select(o => o.TpotMs!.Value), 0.95),
            P99TtftMs = Percentile(all.Where(o => o.TtftMs.HasValue).Select(o => o.TtftMs!.Value), 0.99),
            P99TpotMs = Percentile(all.Where(o => o.TpotMs.HasValue).Select(o => o.TpotMs!.Value), 0.99),
            ArrivalRatePerSecond = arrivalRate,
            AveragePromptTokens = Average(all.Select(o => (double)o.PromptTokens)),
            AverageOutputTokens = Average(all.Select(o => (double)o.OutputTokens)),
            ActiveSequences = active,
            QueueDepth = queued,
            EngineMaxSequences = maxSequences,
            TrainingSampleCount = training.Length,
            TrainingLatestSequence = training.Length == 0 ? 0 : training.Max(o => o.Sequence),
            TrainingP95TtftMs = Percentile(training.Where(o => o.TtftMs.HasValue).Select(o => o.TtftMs!.Value), 0.95),
            TrainingP95TpotMs = Percentile(training.Where(o => o.TpotMs.HasValue).Select(o => o.TpotMs!.Value), 0.95),
            TrainingAverageArrivalRatePerSecond = Average(training.Select(o => o.ArrivalRatePerSecond)),
            TrainingAverageQueueDepth = Average(training.Select(o => (double)o.QueueDepth)),
            TrainingAveragePromptTokens = Average(training.Select(o => (double)o.PromptTokens)),
            TrainingAverageOutputTokens = Average(training.Select(o => (double)o.OutputTokens)),
            TrainingAverageActiveSequences = Average(training.Select(o => (double)o.ActiveSequences)),
            TrainingConcurrency = currentTargetConcurrency,
            FailureCountSinceProcessStart = failures
        };
    }

    private void PruneArrivals(DateTimeOffset cutoff)
    {
        while (_arrivals.Count > 0 && _arrivals.Peek() < cutoff)
            _arrivals.Dequeue();
    }

    private static int SafeInvoke(Func<int>? provider)
    {
        try { return provider?.Invoke() ?? 0; }
        catch { return 0; }
    }

    private static double? Percentile(IEnumerable<double> values, double quantile)
    {
        var data = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        if (data.Length == 0) return null;
        var index = (int)Math.Ceiling(quantile * data.Length) - 1;
        index = Math.Clamp(index, 0, data.Length - 1);
        return data[index];
    }

    private static double Average(IEnumerable<double> values)
    {
        var data = values.Where(double.IsFinite).ToArray();
        return data.Length == 0 ? 0 : data.Average();
    }

    private sealed class PendingRequest
    {
        public PendingRequest(DateTimeOffset startedAt, int promptTokens)
        {
            StartedAt = startedAt;
            PromptTokens = promptTokens;
        }
        public DateTimeOffset StartedAt { get; }
        public int PromptTokens { get; }
        public DateTimeOffset? AdmittedAt { get; set; }
        public int TargetConcurrency { get; set; }
    }
}

public sealed record CompletionObservation(
    long Sequence,
    DateTimeOffset TimestampUtc,
    double? TtftMs,
    double? InferenceTtftMs,
    double? QueueWaitMs,
    double? TpotMs,
    int PromptTokens,
    int OutputTokens,
    int ActiveSequences,
    int QueueDepth,
    int TargetConcurrency,
    double ArrivalRatePerSecond);

public sealed record AdaptiveWindowSnapshot
{
    public DateTimeOffset CreatedAtUtc { get; init; }
    public long LatestSequence { get; init; }
    public int CompletedSamplesInWindow { get; init; }
    public int ValidTtftSamples { get; init; }
    public int ValidTpotSamples { get; init; }
    public double? P95TtftMs { get; init; }
    public double? P95InferenceTtftMs { get; init; }
    public double? P95QueueWaitMs { get; init; }
    public double? P95TpotMs { get; init; }
    public double? P99TtftMs { get; init; }
    public double? P99TpotMs { get; init; }
    public double ArrivalRatePerSecond { get; init; }
    public double AveragePromptTokens { get; init; }
    public double AverageOutputTokens { get; init; }
    public int ActiveSequences { get; init; }
    public int QueueDepth { get; init; }
    public int EngineMaxSequences { get; init; }
    public int TrainingSampleCount { get; init; }
    public long TrainingLatestSequence { get; init; }
    public double? TrainingP95TtftMs { get; init; }
    public double? TrainingP95TpotMs { get; init; }
    public double TrainingAverageArrivalRatePerSecond { get; init; }
    public double TrainingAverageQueueDepth { get; init; }
    public double TrainingAveragePromptTokens { get; init; }
    public double TrainingAverageOutputTokens { get; init; }
    public double TrainingAverageActiveSequences { get; init; }
    public int TrainingConcurrency { get; init; }
    public long FailureCountSinceProcessStart { get; init; }
}
