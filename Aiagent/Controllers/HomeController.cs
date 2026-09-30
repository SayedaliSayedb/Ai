using Aiagent.Models;
using Aiagent.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aiagent.Controllers;

public class HomeController : Controller
{
    private readonly ITextAnalysisQueue _textQueue;
    private readonly ITextAnalysisRepository _textRepository;

    public HomeController(
        ITextAnalysisQueue textQueue,
        ITextAnalysisRepository textRepository)
    {
        _textQueue = textQueue;
        _textRepository = textRepository;
    }

    // ========================= Chat =========================
    [HttpGet("chat")]
    public IActionResult Chat() => View();

    // ========================= Text Analysis =========================
    [HttpGet("text")]
    public IActionResult Text() => View();

    [HttpPost("ask-text")]
    public async Task<IActionResult> AskText(
        [FromForm] string prompt,
        [FromForm] string? systemPrompt)
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
}