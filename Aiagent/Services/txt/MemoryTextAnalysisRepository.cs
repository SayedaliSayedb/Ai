using Aiagent.Models;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System;

namespace Aiagent.Services;

public class MemoryTextAnalysisRepository : ITextAnalysisRepository
{
    private readonly ConcurrentDictionary<Guid, TextAnalysisTask> _store = new();

    public Task AddAsync(TextAnalysisTask task)
    {
        _store.TryAdd(task.Id, task);
        return Task.CompletedTask;
    }

    public Task<TextAnalysisTask?> GetAsync(Guid id)
    {
        _store.TryGetValue(id, out var task);
        return Task.FromResult(task);
    }

    public Task UpdateAsync(TextAnalysisTask task)
    {
        _store[task.Id] = task;
        return Task.CompletedTask;
    }
}