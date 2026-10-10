using Aiagent.Services.Scheduling;
using LLama.Batched;
using System.Threading.Channels;

namespace Aiagent.Models;

public class ConversationState
{
    public required string ConnectionId { get; init; }

    // در حالت صف، ممکن است تا admission مقدار null باشد
    public Conversation? Conversation { get; set; }

    public Channel<string> TokenChannel { get; set; } = Channel.CreateUnbounded<string>();
    public bool IsCompleted { get; set; }
    public bool IsWaitingForPrompt { get; set; }
    public int GeneratedTokens { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? SystemPrompt { get; set; }

    // === فیلدهای TTFT/TPOT ===
    public DateTime? PromptSubmittedAt { get; set; }
    public DateTime? FirstTokenAt { get; set; }
    public DateTime? LastTokenAt { get; set; }

    // === Scheduling (راهکار ۱) ===
    public bool IsAdmitted { get; set; } = true;
    public string? PendingPrompt { get; set; }
    public RequestCost Cost { get; set; } = RequestCost.Empty;
    public DateTimeOffset? EnqueuedAt { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public string? ErrorMessage { get; set; }

    // ⬅️ [KV CACHE POLICY - راهکار ۲]
    /// <summary>تعداد نوبت‌های تکمیل‌شده (برای محاسبه‌ی اولویت).</summary>
    public int TurnCount { get; set; }

    /// <summary>مجموع توکن‌های تولیدشده در همه‌ی نوبت‌ها.</summary>
    public int TotalGeneratedTokens { get; set; }

    /// <summary>آخرین مقدار محاسبه‌شده‌ی اولویت.</summary>
    public double Priority { get; set; }

    /// <summary>زمان آخرین به‌روزرسانی اولویت.</summary>
    public DateTime? LastPriorityUpdate { get; set; }

    /// <summary>آیا این مکالمه به‌دلیل فشار حافظه حذف شده است؟</summary>
    public bool WasEvicted { get; set; }

    /// <summary>دلیل حذف (در صورت وجود).</summary>
    public string? EvictionReason { get; set; }
    // ⬅️ [CHUNKED PREFILL - راهکار ۴]
    /// <summary>قطعات باقی‌مانده‌ی پرامپت برای Prefill تدریجی. null = chunked فعال نیست.</summary>
    public Queue<string>? PrefillChunks { get; set; }

    /// <summary>آیا این مکالمه در حال Prefill تدریجی است؟</summary>
    public bool IsPrefilling { get; set; }
}