using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// وضعیت جاری راهکار ۲ (خاموش/روشن) — thread-safe.
/// </summary>
public sealed class KvCachePolicyState
{
    private volatile bool _enabled;

    public KvCachePolicyState(IOptions<KvCachePolicyOptions> options)
    {
        _enabled = options.Value.Enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}