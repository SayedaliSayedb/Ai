using Aiagent.Models;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// سیاست انتخاب قربانی در زمان اشباع KV Cache.
/// </summary>
public interface IKvCacheEvictionPolicy
{
    /// <summary>
    /// به‌روزرسانی اولویت همه‌ی مکالمات. باید دوره‌ای (مثلاً هر ۱۰۰ms) صدا زده شود.
    /// </summary>
    void UpdatePriorities(IReadOnlyCollection<ConversationState> conversations);

    /// <summary>
    /// انتخاب قربانی برای حذف.
    /// اگر هیچ کاندید مناسبی نبود (همه در حال استریم هستند)، null برمی‌گرداند.
    /// </summary>
    ConversationState? SelectVictim(
        IReadOnlyCollection<ConversationState> conversations,
        string? excludeConnectionId);

    /// <summary>وضعیت جاری برای داشبورد.</summary>
    KvCachePolicySnapshot Snapshot();
}

public readonly record struct KvCachePolicySnapshot(
    int TotalConversations,
    int StreamingConversations,
    int EvictableConversations,
    long TotalEvictions,
    long TotalRejections,
    double LastMinPriority,
    double LastMaxPriority);