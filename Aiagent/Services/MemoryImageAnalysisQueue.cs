using Aiagent.Models;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Aiagent.Services;

public class MemoryImageAnalysisQueue : IImageAnalysisQueue
{
    private readonly ConcurrentQueue<ImageAnalysisTask> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(0);

    public Task AddAsync(ImageAnalysisTask task)
    {
        _queue.Enqueue(task);
        _semaphore.Release();
        return Task.CompletedTask;
    }

    public async Task<ImageAnalysisTask?> DequeueAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        _queue.TryDequeue(out var task);
        return task;
    }
}