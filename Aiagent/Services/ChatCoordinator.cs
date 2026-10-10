using Aiagent.Models;
using Aiagent.Services.Metrics;
using Aiagent.Services.Scheduling;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;
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

    // ⬅️ [SCHEDULING] وابستگی‌های راهکار ۱
    private readonly SchedulingState _schedulingState;
    private readonly IRequestScheduler _scheduler;
    private readonly SchedulingOptions _schedulingOptions;

    // ⬅️ [KV CACHE POLICY] وابستگی‌های راهکار ۲
    private readonly KvCachePolicyState _kvCacheState;
    private readonly IKvCacheEvictionPolicy _kvCachePolicy;
    private readonly KvCachePolicyOptions _kvCacheOptions;

    private int MaxTokensPerResponse { get; }
    private uint SeqMax { get; }

    private const string DefaultSystemPrompt =
        "You are a helpful AI assistant. " +
        "Respond in the same language as the user's question. " +
        "When the user asks for code, write it in a fenced code block. " +
        "For normal greetings and questions, reply with plain text only.";

    private readonly ChunkedPrefillState _chunkedPrefillState;
    private readonly ChunkedPrefillOptions _chunkedPrefillOptions;

    public ChatCoordinator(
        ILogger<ChatCoordinator> logger,
        IConfiguration config,
        IMetricsCollector metrics,
        SchedulingState schedulingState,
        IRequestScheduler scheduler,
        IOptions<SchedulingOptions> schedulingOptions,
        KvCachePolicyState kvCacheState,
        IKvCacheEvictionPolicy kvCachePolicy,
        IOptions<KvCachePolicyOptions> kvCacheOptions,
        ChunkedPrefillState chunkedPrefillState,                   // ⬅️
        IOptions<ChunkedPrefillOptions> chunkedPrefillOptions)     // ⬅️
    {
        // ... انتساب‌های قبلی ...

        _chunkedPrefillState = chunkedPrefillState;                // ⬅️
        _chunkedPrefillOptions = chunkedPrefillOptions.Value;
        // --- وابستگی‌های اصلی (بدون تکرار) ---
        _logger = logger;
        _metrics = metrics;

        // --- راهکار ۱: صف‌بندی ---
        _schedulingState = schedulingState;
        _scheduler = scheduler;
        _schedulingOptions = schedulingOptions.Value;

        // --- راهکار ۲: سیاست KV Cache ---
        _kvCacheState = kvCacheState;
        _kvCachePolicy = kvCachePolicy;
        _kvCacheOptions = kvCacheOptions.Value;

        // --- خواندن تنظیمات LLM ---
        var llm = config.GetSection("Llm");
        var modelPath = llm["ModelPath"] ?? "models/gemma-3-4b-it-Q4_K_M.gguf";

        var gpuLayerCount = llm.GetValue("GpuLayerCount", 99);
        var contextSize = llm.GetValue("ContextSize", 4096u);
        SeqMax = llm.GetValue("SeqMax", 16u);
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

        // ⬅️ استفاده از _kvCacheOptions تا warning ندهد و اطلاعات مفید بدهد
        _logger.LogInformation(
            "✅ مدل بارگذاری شد. | Scheduling.Enabled={Sched} | KvCachePolicy.Enabled={Kv} | W2_Streaming={W2}",
            _schedulingState.Enabled,
            _kvCacheState.Enabled,
            _kvCacheOptions.W2_IsStreaming);
    }

    // =====================================================
    // APIهای عمومی برای Metrics
    // =====================================================
    public int GetActiveConversationCount() => _conversations.Count;

    public int GetActiveSequenceCount()
        => _conversations.Values.Count(c => c.IsAdmitted && !c.IsCompleted && c.Conversation != null);

    public int GetMaxSequences() => (int)SeqMax;

    // ⬅️ [SCHEDULING] صف واقعی + در انتظار نوبت بعدی
    public int GetQueuedCount()
    {
        var waiting = _conversations.Values.Count(
            c => c.IsAdmitted && c.IsWaitingForPrompt && !c.IsCompleted);
        return waiting + _scheduler.PendingCount;
    }

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

    /// <summary>
    /// دسترسی خارجی (برای Controller) به لیست مکالمات — snapshot امن.
    /// </summary>
    public IReadOnlyCollection<ConversationState> GetAllConversations()
        => _conversations.Values.ToList();

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

            var formatted = GemmaPromptFormatter.FormatFirstTurn(
                userMessage,
                !string.IsNullOrWhiteSpace(systemPrompt) ? systemPrompt : DefaultSystemPrompt);

            // ⬅️ [SCHEDULING] محاسبه‌ی هزینه
            var cost = RequestCost.Compute(
                promptLength: RequestCost.ApproximateTokens(formatted),
                expectedOutputLength: RequestCost.EstimateOutputLength(userMessage),
                isFirstTurn: true,
                _schedulingOptions);

            var newState = new ConversationState
            {
                ConnectionId = connectionId,
                Conversation = null,
                SystemPrompt = systemPrompt,
                IsCompleted = false,
                IsWaitingForPrompt = false,
                GeneratedTokens = 0,
                LastActivity = DateTime.UtcNow,
                TokenChannel = Channel.CreateUnbounded<string>(),
                PromptSubmittedAt = DateTime.UtcNow,
                Cost = cost,

                // ⬅️ [KV CACHE POLICY] نوبت اول
                TurnCount = 1,
                TotalGeneratedTokens = 0
            };

            if (_schedulingState.Enabled)
            {
                // === مسیر راهکار ۱: صف ===
                newState.PendingPrompt = formatted;
                newState.IsAdmitted = false;
                newState.EnqueuedAt = DateTimeOffset.UtcNow;

                _conversations.TryAdd(connectionId, newState);
                _scheduler.Enqueue(newState);

                _logger.LogInformation(
                    "🕒 {ConnectionId} در صف زمان‌بندی (cost={Cost:F1}, fast={Fast}, heavy={Heavy})",
                    connectionId, cost.Score, cost.IsFast, cost.IsHeavy);
            }
            else
            {
                // === مسیر Baseline ===
                var conv = _executor.Create();
                conv.Prompt(formatted);

                newState.Conversation = conv;
                newState.IsAdmitted = true;
                newState.AdmittedAt = DateTimeOffset.UtcNow;

                _conversations.TryAdd(connectionId, newState);
                _logger.LogInformation("مکالمه جدید برای {ConnectionId} ایجاد شد.", connectionId);
            }

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

            // ⬅️ [KV CACHE POLICY] ابتدا آمار انباشته، سپس ریست
            state.TotalGeneratedTokens += state.GeneratedTokens;
            state.TurnCount++;

            state.IsCompleted = false;
            state.IsWaitingForPrompt = false;
            state.GeneratedTokens = 0;          // ← حالا ریست امن است
            state.LastActivity = DateTime.UtcNow;
            state.PromptSubmittedAt = DateTime.UtcNow;
            state.FirstTokenAt = null;
            state.LastTokenAt = null;
            state.ErrorMessage = null;
            _decoders.TryRemove(connectionId, out _);

            var formatted = GemmaPromptFormatter.FormatFollowUpTurn(userMessage);

            // ⬅️ [SCHEDULING] محاسبه‌ی هزینه
            var cost = RequestCost.Compute(
                promptLength: RequestCost.ApproximateTokens(formatted),
                expectedOutputLength: RequestCost.EstimateOutputLength(userMessage),
                isFirstTurn: false,
                _schedulingOptions);

            state.Cost = cost;

            if (_schedulingState.Enabled)
            {
                state.PendingPrompt = formatted;
                state.IsAdmitted = false;
                state.EnqueuedAt = DateTimeOffset.UtcNow;

                _scheduler.Enqueue(state);
                _logger.LogInformation(
                    "🕒 پیام بعدی {ConnectionId} در صف (cost={Cost:F1}, turn={Turn})",
                    connectionId, cost.Score, state.TurnCount);
            }
            else
            {
                state.Conversation!.Prompt(formatted);
                state.IsAdmitted = true;
                state.AdmittedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "پیام بعدی برای {ConnectionId} ثبت شد (turn={Turn}).",
                    connectionId, state.TurnCount);
            }
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
            _scheduler.Remove(connectionId);          // ⬅️ [SCHEDULING]
            try { state.TokenChannel.Writer.TryComplete(); } catch { }
            try { state.Conversation?.Dispose(); } catch { }
            _decoders.TryRemove(connectionId, out _);
        }
    }

    // =====================================================
    // حلقه‌ی مرکزی
    // =====================================================

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
                    // ⬅️ [SCHEDULING] مرحله ۰: مدیریت صف
                    if (_schedulingState.Enabled)
                    {
                        ProcessPendingAdmissions();
                    }
                    else if (_scheduler.PendingCount > 0)
                    {
                        FlushPendingAdmissions();
                    }

                    // ⬅️ [KV CACHE POLICY] به‌روزرسانی دوره‌ای اولویت‌ها
                    if (_kvCacheState.Enabled && !_conversations.IsEmpty)
                    {
                        _kvCachePolicy.UpdatePriorities(_conversations.Values.ToList());
                    }
                    // ============================================
                    // ⬅️ [CHUNKED PREFILL] مرحله ۰.۵: تغذیه‌ی قطعات
                    // ============================================
                    if (_chunkedPrefillState.Enabled)
                    {
                        FeedPrefillChunks();
                    }
                    // ============================================
                    // مرحله ۱: Infer
                    // ============================================
                    bool anyRequiresInference = _conversations.Values
                        .Any(c => c.IsAdmitted
                               && !c.IsCompleted
                               && c.Conversation != null
                               && c.Conversation.RequiresInference);

                    if (anyRequiresInference)
                    {
                        var result = await _executor.Infer(stoppingToken);

                        if (result == DecodeResult.NoKvSlot)
                        {
                            _logger.LogWarning("⚠️ KV cache پر شده است.");
                            _metrics.RecordNoKvSlot();

                            // ⬅️ [KV CACHE POLICY] انتخاب سیاست حذف
                            if (_kvCacheState.Enabled)
                                HandleNoKvSlotWithPolicy();
                            else
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

                        // [SCHEDULING] رد درخواست‌های در صف
                        if (!state.IsAdmitted) continue;
                        if (state.Conversation == null) continue;
                        if (state.IsCompleted) continue;
                        if (!state.Conversation.RequiresSampling) continue;
                        // ⬅️ [CHUNKED PREFILL] تا اتمام Prefill، نمونه‌برداری نکن
                        if (state.IsPrefilling) continue;
                        var token = state.Conversation.Sample(sampler);
                        var now = DateTime.UtcNow;

                        // --- TTFT ---
                        if (state.FirstTokenAt == null && state.PromptSubmittedAt.HasValue)
                        {
                            state.FirstTokenAt = now;
                            var ttftMs = (now - state.PromptSubmittedAt.Value).TotalMilliseconds;
                            _metrics.RecordTtft(ttftMs);
                        }

                        // --- TPOT ---
                        if (state.LastTokenAt.HasValue)
                        {
                            var tpotMs = (now - state.LastTokenAt.Value).TotalMilliseconds;
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

                // ============================================
                // تله‌متری
                // ============================================
                var nowTele = DateTime.UtcNow;
                if ((nowTele - lastTelemetry).TotalSeconds >= 5)
                {
                    var activeSeqs = _conversations.Values
                        .Count(c => c.IsAdmitted && !c.IsCompleted
                                 && c.Conversation != null
                                 && c.Conversation.RequiresInference);

                    _logger.LogInformation(
                        "⚡ مکالمات فعال={Active} | صف={Queue} | بچ/ثانیه≈{Batches}/s | توکن/ثانیه≈{Tps}",
                        activeSeqs, _scheduler.PendingCount,
                        telemetryTokens / 5, (int)(telemetryTokens * 1.3 / 5));

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
    // =====================================================
    // ⬅️ [CHUNKED PREFILL] تغذیه‌ی قطعات
    // =====================================================

    /// <summary>
    /// در هر گام، حداکثر MaxChunksPerStep قطعه به مکالمات در حال prefill می‌دهد.
    /// این متد باید داخل _executorLock صدا زده شود.
    /// </summary>
    private void FeedPrefillChunks()
    {
        var budget = _chunkedPrefillOptions.MaxChunksPerStep;
        if (budget <= 0) return;

        foreach (var state in _conversations.Values)
        {
            if (budget <= 0) break;

            if (!state.IsAdmitted) continue;
            if (!state.IsPrefilling) continue;
            if (state.Conversation == null) continue;
            if (state.PrefillChunks == null || state.PrefillChunks.Count == 0)
            {
                state.IsPrefilling = false;
                continue;
            }

            // اگر مکالمه هنوز مشغول Infer قبلی است، صبر کن
            if (state.Conversation.RequiresInference) continue;

            var chunk = state.PrefillChunks.Dequeue();
            state.Conversation.Prompt(chunk);
            budget--;

            if (state.PrefillChunks.Count == 0)
            {
                state.IsPrefilling = false;
                state.PrefillChunks = null;
                _logger.LogDebug(
                    "🧩 {ConnectionId} Prefill کامل شد؛ آماده‌ی Decode.",
                    state.ConnectionId);
            }
        }
    }
    // =====================================================
    // ⬅️ [SCHEDULING] مدیریت صف
    // =====================================================

    private void ProcessPendingAdmissions()
    {
        if (_scheduler.PendingCount == 0) return;

        var activeCount = _conversations.Values.Count(
            c => c.IsAdmitted && !c.IsCompleted && c.Conversation != null);

        var maxActive = _schedulingOptions.MaxActiveConversations > 0
            ? _schedulingOptions.MaxActiveConversations
            : (int)SeqMax;

        var availableSlots = maxActive - activeCount;
        if (availableSlots <= 0) return;

        var admitted = _scheduler.DequeueAdmittable(
            availableSlots,
            _schedulingOptions.MaxHeavyPrefillsPerStep);

        foreach (var state in admitted)
            AdmitState(state, "queue");
    }

    private void FlushPendingAdmissions()
    {
        const int maxFlush = 1000;
        var admitted = _scheduler.DequeueAdmittable(maxFlush, maxFlush);
        foreach (var state in admitted)
            AdmitState(state, "flush");

        if (admitted.Count > 0)
            _logger.LogInformation("🔓 Flush صف: {N} درخواست فوراً پذیرفته شد.", admitted.Count);
    }

    private void AdmitState(ConversationState state, string reason)
    {
        try
        {
            if (state.Conversation == null)
            {
                var conv = _executor.Create();
                state.Conversation = conv;
            }

            // ⬅️ [CHUNKED PREFILL] تصمیم‌گیری درباره‌ی تدریجی بودن
            var prompt = state.PendingPrompt!;
            var useChunked =
                _chunkedPrefillState.Enabled
                && prompt.Length >= _chunkedPrefillOptions.MinLengthToChunk
                && (!_chunkedPrefillOptions.OnlyFirstTurn || state.TurnCount <= 1);

            if (useChunked)
            {
                var chunks = PrefillChunker.Split(
                    prompt, _chunkedPrefillOptions.ChunkSizeCharacters);

                // قطعه‌ی اول را همین حالا Prompt می‌کنیم
                state.Conversation.Prompt(chunks[0]);

                // بقیه در صف می‌مانند
                state.PrefillChunks = new Queue<string>(chunks.Skip(1));
                state.IsPrefilling = state.PrefillChunks.Count > 0;

                _logger.LogInformation(
                    "🧩 {ConnectionId} Chunked Prefill: {N} قطعه (first={Size} chars)",
                    state.ConnectionId, chunks.Count, chunks[0].Length);
            }
            else
            {
                state.Conversation.Prompt(prompt);
                state.PrefillChunks = null;
                state.IsPrefilling = false;
            }

            state.IsAdmitted = true;
            state.AdmittedAt = DateTimeOffset.UtcNow;
            state.PendingPrompt = null;
            state.PromptSubmittedAt = DateTime.UtcNow;

            var waited = state.EnqueuedAt is { } t
                ? (DateTimeOffset.UtcNow - t).TotalMilliseconds
                : 0;

            _logger.LogInformation(
                "✅ {ConnectionId} پذیرفته شد (reason={Reason}, waited={Wait:F0}ms, chunked={C})",
                state.ConnectionId, reason, waited, useChunked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در admission {ConnectionId}", state.ConnectionId);
            state.ErrorMessage = ex.Message;
            state.IsCompleted = true;
            state.IsAdmitted = false;
            state.TokenChannel.Writer.TryComplete();
        }
    }

    // =====================================================
    // ⬅️ [KV CACHE POLICY] مدیریت NoKvSlot با سیاست اولویت‌محور
    // =====================================================

    private void HandleNoKvSlotWithPolicy()
    {
        var snapshot = _conversations.Values.ToList();
        if (snapshot.Count == 0) return;

        // ⚠️ ما داخل _executorLock هستیم؛ RemoveConversation امن است
        // چون Conversation.Dispose روی slot فعلی که Infer شکست خورده تأثیر نمی‌گذارد.
        var victim = _kvCachePolicy.SelectVictim(snapshot, excludeConnectionId: null);

        if (victim == null)
        {
            _logger.LogWarning(
                "🚫 NoKvSlot: هیچ قربانی مناسبی نبود (همه در حال استریم یا محافظت‌شده)؛ ادامه بدون حذف.");
            return;
        }

        victim.WasEvicted = true;
        victim.EvictionReason = "kv_cache_pressure";
        RemoveConversation(victim.ConnectionId);

        _logger.LogWarning(
            "🗑️ KV Cache: قربانی {ConnectionId} حذف شد (priority={P:F2}, turns={T})",
            victim.ConnectionId, victim.Priority, victim.TurnCount);
    }

    // =====================================================
    // کمکی‌ها
    // =====================================================

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