using Aiagent.Services.Metrics;
using Microsoft.AspNetCore.SignalR;

namespace Aiagent.Hubs;

public class MetricsHub : Hub
{
    private readonly IMetricsCollector _collector;
    private readonly ILogger<MetricsHub> _logger;

    public MetricsHub(IMetricsCollector collector, ILogger<MetricsHub> logger)
    {
        _collector = collector;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);

        // ارسال snapshot فوری
        await Clients.Caller.SendAsync("MetricUpdate", _collector.Snapshot());

        await base.OnConnectedAsync();
    }
}