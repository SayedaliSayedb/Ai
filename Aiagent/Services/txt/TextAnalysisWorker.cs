using Aiagent.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aiagent.Services;

public class TextAnalysisWorker : BackgroundService
{
    // ✅ تعداد تسک‌های متنی که هم‌زمان پردازش می‌شوند
    private const int MaxConcurrentTasks = 4;
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);

    private readonly ITextAnalysisQueue _queue;
    private readonly ITextAnalysisRepository _repository;
    private readonly ILogger<TextAnalysisWorker> _logger;
    private readonly ChatCoordinator _coordinator;

    public TextAnalysisWorker(
        ITextAnalysisQueue queue,
        ITextAnalysisRepository repository,
        ILogger<TextAnalysisWorker> logger,
        ChatCoordinator coordinator)
    {
        _queue = queue;
        _repository = repository;
        _logger = logger;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ✅ به جای یک حلقه‌ی سریال، چند پردازشگر موازی اجرا می‌کنیم
        var workers = Enumerable.Range(0, MaxConcurrentTasks)
            .Select(i => ProcessLoopAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ProcessLoopAsync(int workerIndex, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TextAnalysisTask task;

            try
            {
                task = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (task == null) continue;

            var connectionId = $"task-w{workerIndex}-{task.Id:N}";

            try
            {
                task.Status = TextAnalysisStatus.Processing;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);

                _logger.LogInformation("شروع پردازش تسک متنی {TaskId} (worker {Worker})",
                    task.Id, workerIndex);

                var state = await _coordinator.SubmitPromptAsync(
                    connectionId,
                    task.UserPrompt,
                    task.SystemPrompt,
                    stoppingToken);

                using var timeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(TaskTimeout);

                var result = new StringBuilder();

                try
                {
                    await foreach (var token in state.TokenChannel.Reader.ReadAllAsync(timeoutCts.Token))
                    {
                        result.Append(token);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "پاسخ مدل در زمان مقرر (۵ دقیقه) کامل نشد.");
                }

                task.Result = JsonSerializer.Serialize(
                    new { Response = result.ToString().Trim() });
                task.Status = TextAnalysisStatus.Completed;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);

                _logger.LogInformation("تسک متنی {TaskId} با موفقیت پایان یافت.", task.Id);
            }
            catch (Exception ex)
            {
                task.Status = TextAnalysisStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);
                _logger.LogError(ex, "تسک متنی {TaskId} با خطا مواجه شد.", task.Id);
            }
            finally
            {
                _coordinator.RemoveConversation(connectionId);
            }
        }
    }
}