using Aiagent.Services.Adaptive;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/admin/adaptive-controller")]
public sealed class AdaptiveControllerAdminController : ControllerBase
{
    private readonly AdaptiveControllerState _state;

    public AdaptiveControllerAdminController(AdaptiveControllerState state) => _state = state;

    [HttpGet]
    public ActionResult<AdaptiveControllerStatus> GetStatus() => Ok(_state.GetStatus());

    [HttpPost("mode")]
    public IActionResult SetMode([FromBody] SetAdaptiveModeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Mode))
            return BadRequest(new { error = "A mode value is required." });

        if (!_state.Enabled && !string.Equals(request.Mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { error = "AdaptiveController:Enabled is false. Enable it in configuration before changing modes." });

        if (!_state.TrySetMode(request.Mode, out var error))
            return BadRequest(new { error });

        return Ok(_state.GetStatus());
    }
}

public sealed record SetAdaptiveModeRequest(string Mode);
