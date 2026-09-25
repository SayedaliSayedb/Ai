using Aiagent.Models;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Aiagent.Services;

public class MemoryTextAnalysisQueue : ITextAnalysisQueue
{
    private readonly ConcurrentQueue<TextAnalysisTask> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);

    public Task AddAsync(TextAnalysisTask task)
    {
        _queue.Enqueue(task);
        _semaphore.Release();
        return Task.CompletedTask;
    }

    public async Task<TextAnalysisTask?> DequeueAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        _queue.TryDequeue(out var task);
        return task;
    }
}