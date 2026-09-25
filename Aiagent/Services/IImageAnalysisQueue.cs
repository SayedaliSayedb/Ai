using Aiagent.Models;

namespace Aiagent.Services
{
    public interface IImageAnalysisQueue
    {
        Task AddAsync(ImageAnalysisTask task);
        Task<ImageAnalysisTask?> DequeueAsync(CancellationToken cancellationToken);
    }
}
