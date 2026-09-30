using Aiagent.Services.Auth;
using Aiagent.Services.Metrics;
using Aiagent.Services.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

public class DashboardController : Controller
{
    private readonly IMetricsCollector _collector;
    private readonly IStorageService _storage;

    public DashboardController(IMetricsCollector collector, IStorageService storage)
    {
        _collector = collector;
        _storage = storage;
    }

    public IActionResult Index() => View();

    /// <summary>
    /// API برای دریافت snapshot لحظه‌ای
    /// </summary>
    [HttpGet("/api/dashboard/snapshot")]
    public IActionResult Snapshot() => Ok(_collector.Snapshot());

    /// <summary>
    /// دریافت داده‌های تاریخی از فایل‌های Parquet
    /// </summary>
    [HttpGet("/api/dashboard/history")]
    public async Task<IActionResult> History(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to)
    {
        var fromTs = from ?? DateTimeOffset.UtcNow.AddHours(-1);
        var toTs = to ?? DateTimeOffset.UtcNow;

        var files = await _storage.ListAsync("metrics/");
        var result = new List<object>();

        // TODO: پیاده‌سازی خواندن Parquet
        // فعلاً اسکلت باقی می‌ماند

        return Ok(result);
    }
}