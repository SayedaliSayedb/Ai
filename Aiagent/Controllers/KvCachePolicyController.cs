using Aiagent.Models;
using Aiagent.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/kvcache")]
public class KvCachePolicyController : ControllerBase
{
    private readonly KvCachePolicyState _state;
    private readonly IKvCacheEvictionPolicy _policy;
    private readonly KvCachePolicyOptions _options;

    // دسترسی به مکالمات فعال برای گزارش
    private readonly Func<IReadOnlyCollection<ConversationState>> _conversationsProvider;

    public KvCachePolicyController(
        KvCachePolicyState state,
        IKvCacheEvictionPolicy policy,
        IOptions<KvCachePolicyOptions> options,
        IServiceProvider sp)
    {
        _state = state;
        _policy = policy;
        _options = options.Value;

        // ChatCoordinator برای دسترسی به لیست مکالمات
        // (چون Singleton است، این OK است)
        var coordinator = sp.GetRequiredService<Services.ChatCoordinator1>();
        _conversationsProvider = () => coordinator.GetAllConversations();
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var conversations = _conversationsProvider();
        var streaming = 0;
        var evictable = 0;

        foreach (var c in conversations)
        {
            if (c.IsAdmitted && !c.IsCompleted && c.Conversation != null && c.GeneratedTokens > 0)
                streaming++;
            else if (c.IsCompleted || !c.IsAdmitted)
                evictable++;
        }

        var snap = _policy.Snapshot();

        return Ok(new
        {
            enabled = _state.Enabled,
            allowRuntimeToggle = _options.AllowRuntimeToggle,
            options = new
            {
                w1 = _options.W1_RecentActivity,
                w2 = _options.W2_IsStreaming,
                w3 = _options.W3_ConversationLength,
                w4 = _options.W4_RebuildCost,
                activityWindowSeconds = _options.ActivityWindowSeconds,
                maxTurns = _options.MaxTurnsForNormalization,
                completedIdleThresholdSeconds = _options.CompletedIdleThresholdSeconds
            },
            current = new
            {
                totalConversations = conversations.Count,
                streamingConversations = streaming,
                evictableConversations = evictable,
                minPriority = Math.Round(snap.LastMinPriority, 2),
                maxPriority = Math.Round(snap.LastMaxPriority, 2)
            },
            totals = new
            {
                evictions = snap.TotalEvictions,
                rejections = snap.TotalRejections
            }
        });
    }

    public sealed class ToggleRequest
    {
        public bool Enabled { get; set; }
    }

    [HttpPost("toggle")]
    public IActionResult Toggle([FromBody] ToggleRequest req)
    {
        if (!_options.AllowRuntimeToggle)
            return BadRequest(new { error = "تغییر زمان اجرا غیرفعال است." });

        var previous = _state.Enabled;
        _state.Enabled = req.Enabled;

        return Ok(new
        {
            success = true,
            previous,
            enabled = _state.Enabled,
            changedAt = DateTimeOffset.UtcNow
        });
    }
}