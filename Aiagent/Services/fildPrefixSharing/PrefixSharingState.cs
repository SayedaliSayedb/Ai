using Microsoft.Extensions.Options;

namespace Aiagent.Services.Scheduling;

public sealed class PrefixSharingState
{
    private volatile bool _enabled;

    public PrefixSharingState(IOptions<PrefixSharingOptions> options)
    {
        _enabled = options.Value.Enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    // آمار برای داشبورد
    private long _totalHits;
    private long _totalMisses;

    public void RecordHit() => Interlocked.Increment(ref _totalHits);
    public void RecordMiss() => Interlocked.Increment(ref _totalMisses);
    public long TotalHits => Interlocked.Read(ref _totalHits);
    public long TotalMisses => Interlocked.Read(ref _totalMisses);
}