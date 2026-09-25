using Aiagent.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Aiagent.Services;

public interface ITextAnalysisQueue
{
    Task AddAsync(TextAnalysisTask task);
    Task<TextAnalysisTask?> DequeueAsync(CancellationToken cancellationToken);
}