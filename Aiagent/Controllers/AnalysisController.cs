using Aiagent.Models;
using Aiagent.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly IImageAnalysisQueue _queue;
    private readonly IImageAnalysisRepository _repository;

    public AnalysisController(
        IImageAnalysisQueue queue,
        IImageAnalysisRepository repository)
    {
        _queue = queue;
        _repository = repository;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadImage(
        [FromForm] IFormFile file,
        [FromQuery] AnalysisMode mode)
    {
        // اعتبارسنجی فایل
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (file.Length > 10 * 1024 * 1024)
            return BadRequest("File size exceeds 10 MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };
        if (!allowedExtensions.Contains(ext))
            return BadRequest("Unsupported file type.");

        // ذخیره‌سازی امن فایل
        var trustedFileName = $"{Guid.NewGuid()}{ext}";
        var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "_storage");
        Directory.CreateDirectory(storageDir); // ایجاد پوشه اگر وجود ندارد
        var filePath = Path.Combine(storageDir, trustedFileName);

        using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream);
        }

        // ایجاد تسک
        var task = new ImageAnalysisTask
        {
            Id = Guid.NewGuid(),
            StoredFilePath = filePath,
            ContentType = file.ContentType,
            Mode = mode,
            Status = AnalysisTaskStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _queue.AddAsync(task);
        await _repository.AddAsync(task);

        return Accepted(new { taskId = task.Id });
    }

    [HttpGet("status/{taskId}")]
    public async Task<IActionResult> GetStatus(Guid taskId)
    {
        var task = await _repository.GetAsync(taskId);
        if (task == null)
            return NotFound();

        return Ok(new
        {
            task.Status,
            task.Result,
            task.ErrorMessage,
            task.UpdatedAt
        });
    }
}