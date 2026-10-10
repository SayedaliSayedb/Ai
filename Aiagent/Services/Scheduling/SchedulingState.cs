using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// نگه‌دارنده‌ی وضعیت جاری راهکار (خاموش/روشن).
/// Singleton است تا از هر جای برنامه قابل خواندن/تغییر باشد.
/// </summary>
public sealed class SchedulingState
{
    private volatile bool _enabled;

    public SchedulingState(IOptions<SchedulingOptions> options)
    {
        _enabled = options.Value.Enabled;
    }

    /// <summary>وضعیت جاری — خواندن/نوشتن thread-safe است.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}