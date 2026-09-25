using Aiagent.Models;
using Aiagent.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Aiagent.Controllers;

public class HomeController : Controller
{
    // سرویس‌های تصویر
    private readonly IImageAnalysisQueue _imageQueue;
    private readonly IImageAnalysisRepository _imageRepository;

    // سرویس‌های متن (غیراستریم)
    private readonly ITextAnalysisQueue _textQueue;
    private readonly ITextAnalysisRepository _textRepository;

    public HomeController(
        IImageAnalysisQueue imageQueue,
        IImageAnalysisRepository imageRepository,
        ITextAnalysisQueue textQueue,
        ITextAnalysisRepository textRepository)
    {
        _imageQueue = imageQueue;
        _imageRepository = imageRepository;
        _textQueue = textQueue;
        _textRepository = textRepository;
    }

    // ========================= صفحه اصلی =========================
    public IActionResult Index() => View();

    // ========================= اکشن‌های تصویر =========================
    [HttpPost("upload-image")]
    public async Task<IActionResult> UploadImage(IFormFile file, AnalysisMode mode = AnalysisMode.DetailedDescription)
    {
        if (file == null || file.Length == 0)
            return BadRequest("لطفاً یک تصویر انتخاب کنید.");

        if (file.Length > 10 * 1024 * 1024)
            return BadRequest("حجم فایل نباید بیشتر از ۱۰ مگابایت باشد.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };
        if (!allowedExtensions.Contains(ext))
            return BadRequest("فرمت فایل پشتیبانی نمی‌شود.");

        var trustedFileName = $"{Guid.NewGuid()}{ext}";
        var storageDir = Path.Combine(Directory.GetCurrentDirectory(), "_storage");
        Directory.CreateDirectory(storageDir);
        var filePath = Path.Combine(storageDir, trustedFileName);

        using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream);
        }

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

        await _imageQueue.AddAsync(task);
        await _imageRepository.AddAsync(task);

        return Ok(new { taskId = task.Id });
    }

    [HttpGet("task-status/{taskId:guid}")]
    public async Task<IActionResult> GetTaskStatus(Guid taskId)
    {
        var task = await _imageRepository.GetAsync(taskId);
        if (task == null) return NotFound();

        return Ok(new
        {
            task.Status,
            task.Result,
            task.ErrorMessage,
            task.UpdatedAt
        });
    }

    [HttpGet("text")]
    public IActionResult Text() => View();

    [HttpPost("ask-text")]
    public async Task<IActionResult> AskText([FromForm] string prompt, [FromForm] string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return BadRequest("لطفاً یک سوال وارد کنید.");

        var task = new TextAnalysisTask
        {
            Id = Guid.NewGuid(),
            UserPrompt = prompt,
            SystemPrompt = systemPrompt,
            Status = TextAnalysisStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _textQueue.AddAsync(task);
        await _textRepository.AddAsync(task);

        return Ok(new { taskId = task.Id });
    }

    [HttpGet("text-status/{taskId:guid}")]
    public async Task<IActionResult> GetTextStatus(Guid taskId)
    {
        var task = await _textRepository.GetAsync(taskId);
        if (task == null) return NotFound();

        return Ok(new
        {
            task.Status,
            task.Result,
            task.ErrorMessage,
            task.UpdatedAt
        });
    }

    //[HttpGet("chat")]
    public IActionResult Chat()
    {
        return View();
    }
}