using LLama.Batched;
using System.Threading.Channels;

namespace Aiagent.Models;

public class ConversationState
{
    public required string ConnectionId { get; init; }
    public required Conversation Conversation { get; init; }
    public Channel<string> TokenChannel { get; set; } = Channel.CreateUnbounded<string>();
    public bool IsCompleted { get; set; }
    public bool IsWaitingForPrompt { get; set; }
    public int GeneratedTokens { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? SystemPrompt { get; set; }
}