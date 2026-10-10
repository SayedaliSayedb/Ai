using Aiagent.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/chunkedprefill")]
public class ChunkedPrefillController : ControllerBase
{
    private readonly ChunkedPrefillState _state;
    private readonly ChunkedPrefillOptions _options;

    public ChunkedPrefillController(
        ChunkedPrefillState state,
        IOptions<ChunkedPrefillOptions> options)
    {
        _state = state;
        _options = options.Value;
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        enabled = _state.Enabled,
        allowRuntimeToggle = _options.AllowRuntimeToggle,
        options = new
        {
            chunkSizeCharacters = _options.ChunkSizeCharacters,
            maxChunksPerStep = _options.MaxChunksPerStep,
            minLengthToChunk = _options.MinLengthToChunk,
            onlyFirstTurn = _options.OnlyFirstTurn
        }
    });

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