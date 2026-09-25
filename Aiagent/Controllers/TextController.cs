using Aiagent.Models;
using Aiagent.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace Aiagent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TextController : ControllerBase
{
    private readonly ITextAnalysisQueue _queue;
    private readonly ITextAnalysisRepository _repository;

    public TextController(ITextAnalysisQueue queue, ITextAnalysisRepository repository)
    {
        _queue = queue;
        _repository = repository;
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest("متن سوال الزامی است.");

        var task = new TextAnalysisTask
        {
            Id = Guid.NewGuid(),
            UserPrompt = request.Prompt,
            SystemPrompt = request.SystemPrompt,
            Status = TextAnalysisStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _queue.AddAsync(task);
        await _repository.AddAsync(task);

        return Accepted(new { taskId = task.Id });
    }

    [HttpGet("status/{taskId:guid}")]
    public async Task<IActionResult> GetStatus(Guid taskId)
    {
        var task = await _repository.GetAsync(taskId);
        if (task == null) return NotFound();

        return Ok(new
        {
            task.Status,
            task.Result,
            task.ErrorMessage,
            task.UpdatedAt
        });
    }

    public class AskRequest
    {
        public required string Prompt { get; set; }
        public string? SystemPrompt { get; set; }
    }
}