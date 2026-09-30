using Aiagent.Models;
using Aiagent.Services.Metrics;
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
    private readonly IMetricsCollector _metrics;
    private readonly LLamaWeights _model;
    private readonly BatchedExecutor _executor;
    private readonly SemaphoreSlim _executorLock = new(1, 1);

    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly ConcurrentDictionary<string, StreamingTokenDecoder> _decoders = new();

    private int MaxTokensPerResponse { get; }
    private uint SeqMax { get; }   // ← فیلد اضافه شد

    private const string DefaultSystemPrompt =
        "You are a helpful AI assistant. " +
        "Respond in the same language as the user's question. " +
        "When the user asks for code, write it in a fenced code block. " +
        "For normal greetings and questions, reply with plain text only.";

    public ChatCoordinator(
        ILogger<ChatCoordinator> logger,
        IConfiguration config,
        IMetricsCollector metrics)   // ← پارامتر جدید
    {
        _logger = logger;
        _metrics = metrics;

        var llm = config.GetSection("Llm");
        var modelPath = llm["ModelPath"] ?? "models/gemma-3-4b-it-Q4_K_M.gguf";

        var gpuLayerCount = llm.GetValue("GpuLayerCount", 99);
        var contextSize = llm.GetValue("ContextSize", 4096u);
        SeqMax = llm.GetValue("SeqMax", 16u);   // ← ذخیره در فیلد
        var batchSize = llm.GetValue("BatchSize", 512u);
        var uBatchSize = llm.GetValue("UBatchSize", 512u);
        var flashAttention = llm.GetValue("FlashAttention", true);
        var mainGpu = llm.GetValue("MainGpu", 0);
        var threads = llm.GetValue("Threads",
            Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2)));

        MaxTokensPerResponse = llm.GetValue("MaxTokensPerResponse", 1024);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model file not found: {modelPath}");

        _logger.LogInformation("📂 در حال بارگذاری مدل: {ModelPath}", modelPath);
        _logger.LogInformation(
            "⚙️ GPU Layers={GpuLayers} | Context={Context} | SeqMax={SeqMax} | Batch={Batch}/{UBatch} | Threads={Threads} | FA={FlashAttention}",
            gpuLayerCount, contextSize, SeqMax, batchSize, uBatchSize, threads, flashAttention);

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = gpuLayerCount,
            MainGpu = mainGpu,
            BatchSize = batchSize,
            UBatchSize = uBatchSize,
            SeqMax = SeqMax,
            Threads = (int)threads,
            FlashAttention = flashAttention
        };

        _model = LLamaWeights.LoadFromFile(parameters);
        _executor = new BatchedExecutor(_model, parameters);

        _logger.LogInformation("✅ مدل بارگذاری شد.");
    }

    // =====================================================
    // APIهای عمومی برای Metrics
    // =====================================================
    public int GetActiveConversationCount() => _conversations.Count;

    public int GetActiveSequenceCount()
        => _conversations.Values.Count(c => !c.IsCompleted);

    public int GetMaxSequences() => (int)SeqMax;

    public int GetQueuedCount()
        => _conversations.Values.Count(c => c.IsWaitingForPrompt && !c.IsCompleted);

    public double GetKvCacheUsagePercent()
    {
        var active = GetActiveSequenceCount();
        return SeqMax == 0 ? 0.0 : (double)active / SeqMax * 100.0;
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
                TokenChannel = Channel.CreateUnbounded<string>(),
                PromptSubmittedAt = DateTime.UtcNow   // ← ثبت زمان شروع
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

            state.TokenChannel.Writer.TryComplete();
            state.TokenChannel = Channel.CreateUnbounded<string>();

            state.IsCompleted = false;
            state.IsWaitingForPrompt = false;
            state.GeneratedTokens = 0;
            state.LastActivity = DateTime.UtcNow;
            state.PromptSubmittedAt = DateTime.UtcNow;   // ← ریست زمان
            state.FirstTokenAt = null;
            state.LastTokenAt = null;
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
            try { state.TokenChannel.Writer.TryComplete(); } catch { }
            try { state.Conversation.Dispose(); } catch { }
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

                bool didWork = false;

                await _executorLock.WaitAsync(stoppingToken);

                try
                {
                    // ============================================
                    // مرحله ۱: Infer
                    // ============================================
                    bool anyRequiresInference = _conversations.Values
                        .Any(c => c.Conversation.RequiresInference);

                    if (anyRequiresInference)
                    {
                        var result = await _executor.Infer(stoppingToken);

                        if (result == DecodeResult.NoKvSlot)
                        {
                            _logger.LogWarning("⚠️ KV cache پر شده است.");
                            _metrics.RecordNoKvSlot();
                            CleanupInactive(TimeSpan.FromMinutes(5));
                            continue;
                        }

                        if (result != DecodeResult.Ok)
                        {
                            await Task.Delay(50, stoppingToken);
                            continue;
                        }

                        didWork = true;
                        telemetryTokens++;
                        _metrics.RecordBatch();
                    }

                    // ============================================
                    // مرحله ۲: Sample
                    // ============================================
                    foreach (var kvp in _conversations.ToArray())
                    {
                        var connectionId = kvp.Key;
                        var state = kvp.Value;

                        if (state.IsCompleted) continue;
                        if (!state.Conversation.RequiresSampling) continue;

                        var token = state.Conversation.Sample(sampler);
                        var now = DateTime.UtcNow;

                        // --- ثبت TTFT در اولین توکن ---
                        if (state.FirstTokenAt == null &&
                            state.PromptSubmittedAt.HasValue)
                        {
                            state.FirstTokenAt = now;
                            var ttftMs = (now - state.PromptSubmittedAt.Value)
                                .TotalMilliseconds;
                            _metrics.RecordTtft(ttftMs);
                        }

                        // --- ثبت TPOT بین توکن‌های بعدی ---
                        if (state.LastTokenAt.HasValue)
                        {
                            var tpotMs = (now - state.LastTokenAt.Value)
                                .TotalMilliseconds;
                            _metrics.RecordTpot(tpotMs);
                        }
                        state.LastTokenAt = now;

                        state.GeneratedTokens++;
                        state.LastActivity = now;

                        bool isEog = token.IsEndOfGeneration(vocab);
                        bool isLimit = state.GeneratedTokens >= MaxTokensPerResponse;

                        state.Conversation.Prompt(token);

                        if (isEog || isLimit)
                        {
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
                        {
                            state.TokenChannel.Writer.TryWrite(text);
                            _metrics.RecordTokens(1);
                        }

                        didWork = true;
                    }
                }
                finally
                {
                    _executorLock.Release();
                }

                // تله‌متری
                var nowTele = DateTime.UtcNow;
                if ((nowTele - lastTelemetry).TotalSeconds >= 5)
                {
                    var activeSeqs = _conversations.Values
                        .Count(c => !c.IsCompleted && c.Conversation.RequiresInference);

                    _logger.LogInformation(
                        "⚡ مکالمات فعال={Active} | بچ/ثانیه≈{Batches}/s | توکن/ثانیه≈{Tps}",
                        activeSeqs, telemetryTokens / 5, (int)(telemetryTokens * 1.3 / 5));

                    telemetryTokens = 0;
                    lastTelemetry = nowTele;
                }

                if (!didWork)
                    await Task.Delay(10, stoppingToken);
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