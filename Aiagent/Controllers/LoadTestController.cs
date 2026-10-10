using Aiagent.Services.LoadTesting;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

public class LoadTestController : Controller
{
    private readonly LoadTestService _service;

    public LoadTestController(LoadTestService service) => _service = service;

    // -------------- صفحه‌ی UI --------------
    [HttpGet("/loadtest")]
    public IActionResult Index() => View();

    // -------------- API --------------
    [HttpPost("/api/loadtest/start")]
    public async Task<IActionResult> Start(
        [FromBody] LoadTestRequest req, CancellationToken ct)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var run = await _service.StartAsync(req, baseUrl, ct);

        return Ok(new
        {
            runId = run.RunId,
            totalRequests = run.TotalRequests,
            startedAt = run.StartedAt
        });
    }

    [HttpGet("/api/loadtest/status/{runId}")]
    public IActionResult Status(string runId)
    {
        var run = _service.GetRun(runId);
        if (run == null) return NotFound();

        return Ok(new
        {
            runId = run.RunId,
            status = run.Status.ToString(),
            completed = run.CompletedRequests,
            total = run.TotalRequests,
            startedAt = run.StartedAt,
            completedAt = run.CompletedAt,
            error = run.Error
        });
    }

    [HttpGet("/api/loadtest/summary/{runId}")]
    public IActionResult Summary(string runId)
    {
        var s = _service.GetSummary(runId);
        if (s == null) return NotFound();
        return Ok(s);
    }

    [HttpGet("/api/loadtest/results/{runId}")]
    public IActionResult Results(string runId)
    {
        var run = _service.GetRun(runId);
        if (run == null) return NotFound();

        LoadTestResult[] snapshot;
        lock (run.Results) snapshot = run.Results.ToArray();
        return Ok(snapshot);
    }
}