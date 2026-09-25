using Aiagent.Models;
using System;
using System.Threading.Tasks;

namespace Aiagent.Services;

public interface ITextAnalysisRepository
{
    Task AddAsync(TextAnalysisTask task);
    Task<TextAnalysisTask?> GetAsync(Guid id);
    Task UpdateAsync(TextAnalysisTask task);
}