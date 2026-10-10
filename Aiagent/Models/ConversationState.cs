using Aiagent.Services.Scheduling;
using LLama.Batched;
using System.Threading.Channels;

namespace Aiagent.Models;

public class ConversationState
{
    public required string ConnectionId { get; init; }
    public Conversation? Conversation { get; set; }

    public Channel<string> TokenChannel { get; set; } = Channel.CreateUnbounded<string>();
    public bool IsCompleted { get; set; }
    public bool IsWaitingForPrompt { get; set; }
    public int GeneratedTokens { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? SystemPrompt { get; set; }

    // === TTFT/TPOT ===
    public DateTime? PromptSubmittedAt { get; set; }
    public DateTime? FirstTokenAt { get; set; }
    public DateTime? LastTokenAt { get; set; }

    // === Scheduling ===
    public bool IsAdmitted { get; set; } = true;
    public string? PendingPrompt { get; set; }
    public RequestCost Cost { get; set; } = RequestCost.Empty;
    public DateTimeOffset? EnqueuedAt { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public string? ErrorMessage { get; set; }

    // === KV Cache Policy ===
    public int TurnCount { get; set; }
    public int TotalGeneratedTokens { get; set; }
    public double Priority { get; set; }
    public DateTime? LastPriorityUpdate { get; set; }
    public bool WasEvicted { get; set; }
    public string? EvictionReason { get; set; }

    // === Chunked Prefill ===
    public Queue<string>? PrefillChunks { get; set; }
    public bool IsPrefilling { get; set; }

    // ⬅️ [PREFIX SHARING] پیام خالص کاربر (بدون System Prompt)
    /// <summary>پیام خالص کاربر — برای استفاده در PrefixCache.</summary>
    public string? RawUserMessage { get; set; }
}