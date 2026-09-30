using Aiagent.Models;
using Aiagent.Services.Metrics;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/thresholds")]
public class MetricsController : ControllerBase
{
    private readonly IThresholdService _thresholdService;

    public MetricsController(IThresholdService thresholdService)
    {
        _thresholdService = thresholdService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
        => Ok(await _thresholdService.GetAsync());

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] MetricsThreshold threshold)
    {
        if (threshold.TtftP95Max <= 0 || threshold.TpotP95Max <= 0)
            return BadRequest("آستانه‌ها باید بزرگ‌تر از صفر باشند.");

        await _thresholdService.SaveAsync(threshold);
        return Ok(new { success = true, savedAt = DateTimeOffset.UtcNow });
    }

    [HttpGet("breaches")]
    public async Task<IActionResult> Breaches([FromQuery] int count = 100)
        => Ok(await _thresholdService.GetRecentBreachesAsync(count));
}