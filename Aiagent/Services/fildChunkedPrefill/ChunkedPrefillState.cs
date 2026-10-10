using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

public sealed class ChunkedPrefillState
{
    private volatile bool _enabled;

    public ChunkedPrefillState(IOptions<ChunkedPrefillOptions> options)
    {
        _enabled = options.Value.Enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}