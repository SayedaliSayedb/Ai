using Aiagent.Models;

namespace Aiagent.Services
{
    public interface IImageAnalysisRepository
    {
        Task AddAsync(ImageAnalysisTask task);
        Task<ImageAnalysisTask?> GetAsync(Guid id);
        Task UpdateAsync(ImageAnalysisTask task);
    }
}
