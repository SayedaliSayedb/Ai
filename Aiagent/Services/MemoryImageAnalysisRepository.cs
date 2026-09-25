using Aiagent.Models;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Aiagent.Services;

public class MemoryImageAnalysisRepository : IImageAnalysisRepository
{
    private readonly ConcurrentDictionary<Guid, ImageAnalysisTask> _store = new();

    public Task AddAsync(ImageAnalysisTask task)
    {
        _store.TryAdd(task.Id, task);
        return Task.CompletedTask;
    }

    public Task<ImageAnalysisTask?> GetAsync(Guid id)
    {
        _store.TryGetValue(id, out var task);
        return Task.FromResult(task);
    }

    public Task UpdateAsync(ImageAnalysisTask task)
    {
        _store[task.Id] = task;
        return Task.CompletedTask;
    }
}