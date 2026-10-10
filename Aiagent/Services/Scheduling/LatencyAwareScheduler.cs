using Aiagent.Models;
using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// صف اولویت‌دار دو‌سطحی:
///   صف سریع — درخواست‌های با هزینه‌ی کم
///   صف کند  — بقیه
/// عملیات درج/برداشت/حذف O(1) (LinkedList + Dictionary).
/// </summary>
public sealed class LatencyAwareScheduler : IRequestScheduler
{
    private readonly ILogger<LatencyAwareScheduler> _logger;
    private readonly SchedulingOptions _options;

    private readonly object _lock = new();
    private readonly LinkedList<ConversationState> _fast = new();
    private readonly LinkedList<ConversationState> _slow = new();
    private readonly Dictionary<string, LinkedListNode<ConversationState>> _index = new();

    private long _totalAdmitted;
    private long _totalRejected;

    public LatencyAwareScheduler(
        ILogger<LatencyAwareScheduler> logger,
        IOptions<SchedulingOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public int PendingCount
    {
        get { lock (_lock) return _fast.Count + _slow.Count; }
    }

    public long TotalAdmitted => Interlocked.Read(ref _totalAdmitted);
    public long TotalRejected => Interlocked.Read(ref _totalRejected);

    public void Enqueue(ConversationState state)
    {
        // ⬅️ دیگر Value لازم نیست؛ Cost غیر nullable است
        state.EnqueuedAt ??= DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (_fast.Count + _slow.Count >= _options.MaxPendingQueueSize)
            {
                Interlocked.Increment(ref _totalRejected);
                _logger.LogWarning(
                    "صف پر است؛ درخواست {ConnectionId} رد شد (queue={Q})",
                    state.ConnectionId, _fast.Count + _slow.Count);
                RejectState(state, "صف سرویس پر است؛ لطفاً کمی بعد تلاش کنید.");
                return;
            }

            var target = state.Cost.IsFast ? _fast : _slow;
            var node = target.AddLast(state);
            _index[state.ConnectionId] = node;

            _logger.LogDebug(
                "صف‌بندی {ConnectionId}: {Tier} (score={Score:F1}, heavy={Heavy})",
                state.ConnectionId,
                state.Cost.IsFast ? "FAST" : "SLOW",
                state.Cost.Score,
                state.Cost.IsHeavy);
        }
    }

    public IReadOnlyList<ConversationState> DequeueAdmittable(
        int availableSlots, int maxHeavyPrefills)
    {
        if (availableSlots <= 0) return Array.Empty<ConversationState>();

        var admitted = new List<ConversationState>(Math.Min(availableSlots, 64));
        var now = DateTimeOffset.UtcNow;
        var timeout = _options.PendingTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_options.PendingTimeoutSeconds)
            : Timeout.InfiniteTimeSpan;

        lock (_lock)
        {
            int heavyCount = 0;

            // ---- مرحله ۱: تخلیه‌ی صف سریع ----
            while (admitted.Count < availableSlots && _fast.First is { } node)
            {
                _fast.RemoveFirst();
                _index.Remove(node.Value.ConnectionId);
                admitted.Add(node.Value);
            }

            // ---- مرحله ۲: تخلیه‌ی صف کند (با محدودیت Prefill سنگین) ----
            while (admitted.Count < availableSlots && _slow.First is { } node)
            {
                var state = node.Value;

                if (state.Cost.IsHeavy && heavyCount >= maxHeavyPrefills)
                    break;

                _slow.RemoveFirst();
                _index.Remove(state.ConnectionId);
                admitted.Add(state);
                if (state.Cost.IsHeavy) heavyCount++;
            }

            // ---- مرحله ۳: انقضای timeout ----
            ExpireTimedOut(now, timeout);
        }

        Interlocked.Add(ref _totalAdmitted, admitted.Count);
        return admitted;
    }

    public void Remove(string connectionId)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(connectionId, out var node))
            {
                node.List?.Remove(node);
                _index.Remove(connectionId);
            }
        }
    }

    public SchedulerSnapshot Snapshot()
    {
        lock (_lock)
        {
            double oldestMs = 0;
            var oldest = _fast.First?.Value ?? _slow.First?.Value;
            if (oldest?.EnqueuedAt is { } t)
                oldestMs = (DateTimeOffset.UtcNow - t).TotalMilliseconds;

            return new SchedulerSnapshot(
                FastCount: _fast.Count,
                SlowCount: _slow.Count,
                TotalPending: _fast.Count + _slow.Count,
                TotalAdmitted: Interlocked.Read(ref _totalAdmitted),
                TotalRejected: Interlocked.Read(ref _totalRejected),
                OldestPendingMs: oldestMs);
        }
    }

    // ===== Helpers =====

    private void ExpireTimedOut(DateTimeOffset now, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return;
        ExpireFrom(_fast);
        ExpireFrom(_slow);

        void ExpireFrom(LinkedList<ConversationState> q)
        {
            while (q.First is { } n && now - n.Value.EnqueuedAt!.Value > timeout)
            {
                q.RemoveFirst();
                _index.Remove(n.Value.ConnectionId);
                Interlocked.Increment(ref _totalRejected);
                RejectState(n.Value, "زمان انتظار در صف به پایان رسید.");
                _logger.LogWarning(
                    "درخواست {ConnectionId} به دلیل timeout از صف حذف شد.",
                    n.Value.ConnectionId);
            }
        }
    }

    private static void RejectState(ConversationState state, string message)
    {
        state.ErrorMessage = message;
        state.IsCompleted = true;
        state.IsAdmitted = false;
        try { state.TokenChannel.Writer.TryComplete(); } catch { }
    }
}