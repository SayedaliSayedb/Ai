using Aiagent.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/scheduling")]
public class SchedulingController : ControllerBase
{
    private readonly SchedulingState _state;
    private readonly IRequestScheduler _scheduler;
    private readonly SchedulingOptions _options;

    public SchedulingController(
        SchedulingState state,
        IRequestScheduler scheduler,
        IOptions<SchedulingOptions> options)
    {
        _state = state;
        _scheduler = scheduler;
        _options = options.Value;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var snap = _scheduler.Snapshot();
        return Ok(new
        {
            enabled = _state.Enabled,
            allowRuntimeToggle = _options.AllowRuntimeToggle,
            options = new
            {
                fastQueueThreshold = _options.FastQueueThreshold,
                alpha = _options.Alpha,
                beta = _options.Beta,
                gamma = _options.Gamma,
                maxHeavyPrefillsPerStep = _options.MaxHeavyPrefillsPerStep,
                maxPendingQueueSize = _options.MaxPendingQueueSize,
                pendingTimeoutSeconds = _options.PendingTimeoutSeconds,
                maxActiveConversations = _options.MaxActiveConversations,
                heavyPrefillPromptThreshold = _options.HeavyPrefillPromptThreshold
            },
            queue = new
            {
                fastCount = snap.FastCount,
                slowCount = snap.SlowCount,
                totalPending = snap.TotalPending,
                totalAdmitted = snap.TotalAdmitted,
                totalRejected = snap.TotalRejected,
                oldestPendingMs = Math.Round(snap.OldestPendingMs, 1)
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