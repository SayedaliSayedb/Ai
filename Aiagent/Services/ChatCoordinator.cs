using Aiagent.Models;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Aiagent.Services;

public class ChatCoordinator : BackgroundService
{
    private readonly ILogger<ChatCoordinator> _logger;
    private readonly LLamaWeights _model;
    private readonly BatchedExecutor _executor;
    private readonly SemaphoreSlim _executorLock = new(1, 1);

    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly ConcurrentDictionary<string, StreamingTokenDecoder> _decoders = new();

    private const int MaxTokensPerResponse = 1024;

    private const string DefaultSystemPrompt =
        "You are a helpful AI assistant. " +
        "Respond in the same language as the user's question. " +
        "When the user asks for code, write it in a fenced code block. " +
        "For normal greetings and questions, reply with plain text only.";

    public ChatCoordinator(ILogger<ChatCoordinator> logger, IConfiguration config)
    {
        _logger = logger;

        var llm = config.GetSection("Llm");
        var modelPath = llm["ModelPath"] ?? "models/gemma-3-4b-it-Q4_K_M.gguf";

        var gpuLayerCount = llm.GetValue("GpuLayerCount", 99);
        var contextSize = llm.GetValue("ContextSize", 4096u);
        var seqMax = llm.GetValue("SeqMax", 8u);
        var batchSize = llm.GetValue("BatchSize", 512u);
        var uBatchSize = llm.GetValue("UBatchSize", 512u);
        var flashAttention = llm.GetValue("FlashAttention", true);
        var threads = llm.GetValue("Threads", Math.Max(1, Environment.ProcessorCount - 2));

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model file not found: {modelPath}");

        _logger.LogInformation("📂 در حال بارگذاری مدل: {ModelPath}", modelPath);

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayerCount,
            BatchSize = batchSize,
            UBatchSize = uBatchSize,
            SeqMax = seqMax,
            Threads = (int)threads,
            FlashAttention = flashAttention
        };

        _model = LLamaWeights.LoadFromFile(parameters);
        _executor = new BatchedExecutor(_model, parameters);

        _logger.LogInformation("✅ مدل بارگذاری شد.");
    }

    // =====================================================
    // کمکی‌ها برای ChatHub
    // =====================================================

    public bool HasConversation(string connectionId)
        => _conversations.ContainsKey(connectionId);

    public ConversationState? GetConversation(string connectionId)
        => _conversations.TryGetValue(connectionId, out var s) ? s : null;

    // =====================================================
    // پیام اول
    // =====================================================
    public async Task<ConversationState> SubmitPromptAsync(
        string connectionId,
        string userMessage,
        string? systemPrompt,
        CancellationToken cancellationToken)
    {
        await _executorLock.WaitAsync(cancellationToken);
        try
        {
            if (_conversations.TryGetValue(connectionId, out var existingState))
            {
                existingState.LastActivity = DateTime.UtcNow;
                return existingState;
            }

            var conv = _executor.Create();

            var formatted = GemmaPromptFormatter.FormatFirstTurn(
                userMessage,
                !string.IsNullOrWhiteSpace(systemPrompt) ? systemPrompt : DefaultSystemPrompt);

            conv.Prompt(formatted);

            var newState = new ConversationState
            {
                ConnectionId = connectionId,
                Conversation = conv,
                SystemPrompt = systemPrompt,
                IsCompleted = false,
                IsWaitingForPrompt = false,
                GeneratedTokens = 0,
                LastActivity = DateTime.UtcNow,
                TokenChannel = Channel.CreateUnbounded<string>()
            };

            _conversations.TryAdd(connectionId, newState);
            _logger.LogInformation("مکالمه جدید برای {ConnectionId} ایجاد شد.", connectionId);

            return newState;
        }
        finally
        {
            _executorLock.Release();
        }
    }

    // =====================================================
    // پیام‌های بعدی
    // =====================================================
    public async Task SubmitFollowUpAsync(
        string connectionId,
        string userMessage,
        CancellationToken cancellationToken)
    {
        await _executorLock.WaitAsync(cancellationToken);
        try
        {
            if (!_conversations.TryGetValue(connectionId, out var state))
                return;

            state.TokenChannel = Channel.CreateUnbounded<string>();
            state.IsCompleted = false;
            state.IsWaitingForPrompt = false;
            state.GeneratedTokens = 0;
            state.LastActivity = DateTime.UtcNow;
            _decoders.TryRemove(connectionId, out _);

            var formatted = GemmaPromptFormatter.FormatFollowUpTurn(userMessage);
            state.Conversation.Prompt(formatted);

            _logger.LogInformation("پیام بعدی برای {ConnectionId} ثبت شد.", connectionId);
        }
        finally
        {
            _executorLock.Release();
        }
    }

    public void RemoveConversation(string connectionId)
    {
        if (_conversations.TryRemove(connectionId, out var state))
        {
            try { state.Conversation.Dispose(); } catch { }
            try { state.TokenChannel.Writer.TryComplete(); } catch { }
            _decoders.TryRemove(connectionId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🔄 حلقه‌ی مرکزی شروع به کار کرد.");
        await RunInferenceLoopAsync(stoppingToken);
    }

    private async Task RunInferenceLoopAsync(CancellationToken stoppingToken)
    {
        var sampler = new DefaultSamplingPipeline
        {
            Temperature = 0.3f,
            TopP = 0.9f,
            RepeatPenalty = 1.3f,
            FrequencyPenalty = 0.5f,
            PresencePenalty = 0.3f
        };

        var vocab = _executor.Context.NativeHandle.ModelHandle.Vocab;
        var lastTelemetry = DateTime.UtcNow;
        var telemetryTokens = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_conversations.IsEmpty)
                {
                    await Task.Delay(50, stoppingToken);
                    continue;
                }

                await _executorLock.WaitAsync(stoppingToken);

                try
                {
                    // ============================================
                    // مرحله ۱: Infer — شامل مکالمات completed
                    // که هنوز EOG شان پردازش نشده (RequiresInference)
                    // ============================================
                    bool anyRequiresInference = _conversations.Values
                        .Any(c => c.Conversation.RequiresInference);

                    if (anyRequiresInference)
                    {
                        var result = await _executor.Infer(stoppingToken);

                        if (result == DecodeResult.NoKvSlot)
                        {
                            _logger.LogWarning("⚠️ KV cache پر شده است.");
                            CleanupInactive(TimeSpan.FromMinutes(5));
                            continue;
                        }

                        if (result != DecodeResult.Ok)
                        {
                            await Task.Delay(50, stoppingToken);
                            continue;
                        }

                        telemetryTokens++;
                    }

                    // ============================================
                    // مرحله ۲: Sample — فقط مکالمات فعال
                    // ============================================
                    foreach (var kvp in _conversations.ToArray())
                    {
                        var connectionId = kvp.Key;
                        var state = kvp.Value;

                        if (state.IsCompleted) continue;
                        if (!state.Conversation.RequiresSampling) continue;

                        var token = state.Conversation.Sample(sampler);

                        state.GeneratedTokens++;
                        state.LastActivity = DateTime.UtcNow;

                        bool isEog = token.IsEndOfGeneration(vocab);
                        bool isLimit = state.GeneratedTokens >= MaxTokensPerResponse;

                        // ✅ توکن را (حتی EOG را) به KV برمی‌گردانیم
                        // تا مدل مرز نوبت را ببیند.
                        state.Conversation.Prompt(token);

                        if (isEog || isLimit)
                        {
                            // IsCompleted را ست می‌کنیم اما Infer بعدی
                            // هنوز باید یک بار برای این مکالمه اجرا شود
                            // تا توکن EOG مصرف شود. این کار خودکار توسط
                            // شرط anyRequiresInference در بالا انجام می‌شود.
                            state.IsCompleted = true;
                            state.IsWaitingForPrompt = true;
                            state.TokenChannel.Writer.TryComplete();
                            _decoders.TryRemove(connectionId, out _);

                            _logger.LogInformation(
                                "پاسخ {ConnectionId} کامل شد. (توکن: {Count})",
                                connectionId, state.GeneratedTokens);
                            continue;
                        }

                        // دیکود و ارسال
                        if (!_decoders.TryGetValue(connectionId, out var decoder))
                        {
                            decoder = new StreamingTokenDecoder(_executor.Context)
                            {
                                DecodeSpecialTokens = false
                            };
                            _decoders[connectionId] = decoder;
                        }

                        decoder.Add(token);
                        var text = CleanChunk(decoder.Read());

                        if (!string.IsNullOrEmpty(text))
                            state.TokenChannel.Writer.TryWrite(text);
                    }
                }
                finally
                {
                    _executorLock.Release();
                }

                // تله‌متری
                var now = DateTime.UtcNow;
                if ((now - lastTelemetry).TotalSeconds >= 5)
                {
                    var activeSeqs = _conversations.Values
                        .Count(c => !c.IsCompleted && c.Conversation.RequiresInference);

                    _logger.LogInformation(
                        "⚡ مکالمات فعال={Active} | بچ/ثانیه≈{Batches}/s | توکن/ثانیه≈{Tps}",
                        activeSeqs, telemetryTokens / 5, (int)(telemetryTokens * 1.3 / 5));

                    telemetryTokens = 0;
                    lastTelemetry = now;
                }

                await Task.Delay(5, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "خطا در حلقه‌ی مرکزی");
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    private void CleanupInactive(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        var inactive = _conversations
            .Where(kvp => now - kvp.Value.LastActivity > timeout)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in inactive)
            RemoveConversation(key);
    }

    private static string CleanChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return string.Empty;
        return Regex.Replace(chunk, @"<0x([0-9A-Fa-f]{2})>",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    public override void Dispose()
    {
        try { _executor?.Dispose(); } catch { }
        try { _model?.Dispose(); } catch { }
        _executorLock.Dispose();
        base.Dispose();
    }
}

// =====================================================
// فرمتر مخصوص Gemma 3
// =====================================================
public static class GemmaPromptFormatter
{
    /// <summary>
    /// نوبت اول: شامل system prompt و user message
    /// فرمت خروجی:
    ///   <start_of_turn>user
    ///   {system}
    ///   
    ///   {user}<end_of_turn>
    ///   <start_of_turn>model
    /// </summary>
    public static string FormatFirstTurn(string userMessage, string? systemPrompt = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("<start_of_turn>user\n");

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append(systemPrompt);
            sb.Append("\n\n");
        }

        sb.Append(userMessage);
        sb.Append("<end_of_turn>\n");
        sb.Append("<start_of_turn>model\n");

        return sb.ToString();
    }
    public static string FormatFollowUpTurn(string userMessage)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("<start_of_turn>user\n");
        sb.Append(userMessage);
        sb.Append("<end_of_turn>\n");
        sb.Append("<start_of_turn>model\n");

        return sb.ToString();
    }
}