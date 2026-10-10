using Aiagent.Models;
using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// سیاست اولویت‌محور برای حذف مکالمات در زمان NoKvSlot.
///
/// فرمول اولویت:
///   priority = w₁·recent + w₂·is_streaming + w₃·length − w₄·rebuild_cost
///
/// قاعده‌ی مطلق: مکالمه‌ی در حال استریم هرگز حذف نمی‌شود.
/// </summary>
public sealed class PriorityKvCachePolicy : IKvCacheEvictionPolicy
{
    private readonly ILogger<PriorityKvCachePolicy> _logger;
    private readonly KvCachePolicyOptions _options;

    private long _totalEvictions;
    private long _totalRejections;
    private double _lastMinPriority;
    private double _lastMaxPriority;

    public PriorityKvCachePolicy(
        ILogger<PriorityKvCachePolicy> logger,
        IOptions<KvCachePolicyOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public void UpdatePriorities(IReadOnlyCollection<ConversationState> conversations)
    {
        if (conversations.Count == 0) return;

        var now = DateTime.UtcNow;
        double min = double.MaxValue, max = double.MinValue;

        foreach (var c in conversations)
        {
            c.Priority = ComputePriority(c, now);
            c.LastPriorityUpdate = now;
            if (c.Priority < min) min = c.Priority;
            if (c.Priority > max) max = c.Priority;
        }

        _lastMinPriority = min;
        _lastMaxPriority = max;
    }

    public ConversationState? SelectVictim(
        IReadOnlyCollection<ConversationState> conversations,
        string? excludeConnectionId)
    {
        if (conversations.Count == 0) return null;

        // اولویت‌ها را تازه کن
        UpdatePriorities(conversations);

        ConversationState? victim = null;
        double lowest = double.MaxValue;

        foreach (var c in conversations)
        {
            // خودِ درخواست فعلی که NoKvSlot را تحریک کرده، کاندید نیست
            if (c.ConnectionId == excludeConnectionId) continue;

            // قاعده‌ی طلایی: مکالمه‌ی در حال استریم هرگز حذف نمی‌شود
            if (IsStreaming(c)) continue;

            // فقط مکالماتی که قبلاً تکمیل شده‌اند یا در انتظار نوبت بعدی هستند
            if (!c.IsCompleted && c.IsAdmitted) continue;

            if (c.Priority < lowest)
            {
                lowest = c.Priority;
                victim = c;
            }
        }

        if (victim == null)
        {
            Interlocked.Increment(ref _totalRejections);
            _logger.LogWarning(
                "🚫 هیچ قربانی مناسبی برای حذف پیدا نشد (exclude={Exclude}); درخواست جدید رد می‌شود.",
                excludeConnectionId);
            return null;
        }

        Interlocked.Increment(ref _totalEvictions);
        _logger.LogWarning(
            "🗑️ قربانی انتخاب شد: {ConnectionId} (priority={P:F2}, wasStreaming={S}, turns={T})",
            victim.ConnectionId, victim.Priority, IsStreaming(victim), victim.TurnCount);

        return victim;
    }

    public KvCachePolicySnapshot Snapshot()
    {
        return new KvCachePolicySnapshot(
            TotalConversations: 0,
            StreamingConversations: 0,
            EvictableConversations: 0,
            TotalEvictions: Interlocked.Read(ref _totalEvictions),
            TotalRejections: Interlocked.Read(ref _totalRejections),
            LastMinPriority: _lastMinPriority,
            LastMaxPriority: _lastMaxPriority);
    }

    // =====================================================
    // محاسبه‌ی اولویت
    // =====================================================

    private double ComputePriority(ConversationState c, DateTime now)
    {
        // --- recent_activity: هرچه آخرین فعالیت جدیدتر، بالاتر ---
        var secondsSinceActivity = (now - c.LastActivity).TotalSeconds;
        var recent = 1.0 / (1.0 + secondsSinceActivity / _options.ActivityWindowSeconds);

        // --- is_streaming: مطلق (عملاً veto) ---
        var streaming = IsStreaming(c) ? 1.0 : 0.0;

        // --- conversation_length: نرمال‌شده بین ۰ و ۱ ---
        var length = Math.Min(1.0,
            (double)c.TurnCount / Math.Max(1, _options.MaxTurnsForNormalization));

        // --- rebuild_cost: هزینه‌ی بازسازی در صورت حذف ---
        var rebuild = ComputeRebuildCost(c);

        return _options.W1_RecentActivity * recent
             + _options.W2_IsStreaming * streaming
             + _options.W3_ConversationLength * length
             - _options.W4_RebuildCost * rebuild;
    }

    private double ComputeRebuildCost(ConversationState c)
    {
        // هزینه‌ی System Prompt (نرمال‌شده)
        var sysTokens = RequestCost.ApproximateTokens(c.SystemPrompt ?? "");
        var sysCost = Math.Min(1.0, sysTokens / 500.0);

        // هزینه‌ی نوبت‌های قبلی
        var turnCost = Math.Min(1.0,
            (double)c.TurnCount / Math.Max(1, _options.MaxTurnsForNormalization));

        // هزینه‌ی کل توکن‌های تولیدشده
        var tokenCost = Math.Min(1.0, c.TotalGeneratedTokens / 4000.0);

        return 0.5 * sysCost + 0.3 * turnCost + 0.2 * tokenCost;
    }

    /// <summary>
    /// «در حال استریم» = در batch فعال است و پاسخ در حال تولید.
    /// مکالمه‌ی IsCompleted && IsWaitingForPrompt در حال استریم نیست.
    /// </summary>
    private static bool IsStreaming(ConversationState c)
    {
        return c.IsAdmitted
            && !c.IsCompleted
            && c.Conversation != null
            && c.GeneratedTokens > 0;   // حداقل یک توکن تولید شده
    }
}