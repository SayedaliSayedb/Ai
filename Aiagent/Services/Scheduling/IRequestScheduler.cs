using Aiagent.Models;

namespace Aiagent.Services.Scheduling;

public interface IRequestScheduler
{
    int PendingCount { get; }
    long TotalAdmitted { get; }
    long TotalRejected { get; }

    void Enqueue(ConversationState state);
    IReadOnlyList<ConversationState> DequeueAdmittable(int availableSlots, int maxHeavyPrefills);
    void Remove(string connectionId);
    SchedulerSnapshot Snapshot();
}

public readonly record struct SchedulerSnapshot(
    int FastCount,
    int SlowCount,
    int TotalPending,
    long TotalAdmitted,
    long TotalRejected,
    double OldestPendingMs);