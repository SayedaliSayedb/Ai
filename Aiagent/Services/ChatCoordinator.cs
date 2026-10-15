using Aiagent.Models;
using Aiagent.Services.Metrics;
using Aiagent.Services.Scheduling;
using Aiagent.Services.Adaptive;
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

    // === راهکار ۱: صف‌بندی ===
    private readonly SchedulingState _schedulingState;
    private readonly IRequestScheduler _scheduler;
    private readonly SchedulingOptions _schedulingOptions;

    // === راهکار ۲: KV Cache Policy ===
    private readonly KvCachePolicyState _kvCacheState;
    private readonly IKvCacheEvictionPolicy _kvCachePolicy;
    private readonly KvCachePolicyOptions _kvCacheOptions;

    // === راهکار ۴: Chunked Prefill ===
    private readonly ChunkedPrefillState _chunkedPrefillState;
    private readonly ChunkedPrefillOptions _chunkedPrefillOptions;

    // === راهکار ۳: Prefix Sharing ===
    private readonly PrefixSharingState _prefixSharingState;
    private readonly PrefixSharingOptions _prefixSharingOptions;
    private readonly PrefixCache _prefixCache;
    private readonly AdaptiveTelemetryStore _adaptiveTelemetry;
    private readonly AdaptiveControllerState _adaptiveControllerState;

    private int MaxTokensPerResponse { get; }
    private uint SeqMax { get; }

    private const string DefaultSystemPrompt =
        "You are a helpful AI assistant. " +
        "Respond in the same language as the user's question. " +
        "When the user asks for code, write it in a fenced code block. " +
        "For normal greetings and questions, reply with plain text only.";

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
        ChunkedPrefillState chunkedPrefillState,
        IOptions<ChunkedPrefillOptions> chunkedPrefillOptions,
        PrefixSharingState prefixSharingState,
        IOptions<PrefixSharingOptions> prefixSharingOptions,
        PrefixCache prefixCache,
        AdaptiveTelemetryStore adaptiveTelemetry,
        AdaptiveControllerState adaptiveControllerState)
    {
        _logger = logger;
        _metrics = metrics;

        _schedulingState = schedulingState;
        _scheduler = scheduler;
        _schedulingOptions = schedulingOptions.Value;

        _kvCacheState = kvCacheState;
        _kvCachePolicy = kvCachePolicy;
        _kvCacheOptions = kvCacheOptions.Value;

        _chunkedPrefillState = chunkedPrefillState;
        _chunkedPrefillOptions = chunkedPrefillOptions.Value;

        _prefixSharingState = prefixSharingState;
        _prefixSharingOptions = prefixSharingOptions.Value;
        _prefixCache = prefixCache;
        _adaptiveTelemetry = adaptiveTelemetry;
        _adaptiveControllerState = adaptiveControllerState;

        var llm = config.GetSection("Llm");
        var modelPath = llm["ModelPath"] ?? "models/gemma-3-4b-it-Q4_K_M.gguf";

        var gpuLayerCount = llm.GetValue("GpuLayerCount", 99);
        var contextSize = llm.GetValue("ContextSize", 4096u);
        SeqMax = llm.GetValue("SeqMax", 8u);
        var schedulerHardLimit = _schedulingOptions.MaxActiveConversations > 0
            ? Math.Min(_schedulingOptions.MaxActiveConversations, (int)SeqMax)
            : (int)SeqMax;
        _adaptiveControllerState.ConfigureRuntimeMaximum(schedulerHardLimit);
        var batchSize = llm.GetValue("BatchSize", 512u);
        var uBatchSize = llm.GetValue("UBatchSize", 512u);
        var flashAttention = llm.GetValue("FlashAttention", true);
        var mainGpu = llm.GetValue("MainGpu", 0);
        var threads = llm.GetValue("Threads",
            Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2)));

        MaxTokensPerResponse = llm.GetValue("MaxTokensPerResponse", 4096);

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

        _logger.LogInformation(
            "✅ مدل بارگذاری شد. | Sched={S} | KvCache={K} | Chunked={C} | PrefixCache={P}",
            _schedulingState.Enabled,
            _kvCacheState.Enabled,
            _chunkedPrefillState.Enabled,
            _prefixSharingState.Enabled);
    }

    // =====================================================
    // APIهای عمومی برای Metrics
    // =====================================================
    public int GetActiveConversationCount() => _conversations.Count;

    public int GetActiveSequenceCount()
        => _conversations.Values.Count(c => c.IsAdmitted && !c.IsCompleted && c.Conversation != null);

    public int GetMaxSequences() => (int)SeqMax;

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

            var effectiveSystemPrompt = !string.IsNullOrWhiteSpace(systemPrompt)
                ? systemPrompt
                : DefaultSystemPrompt;

            var formatted = GemmaPromptFormatter.FormatFirstTurn(
                userMessage, effectiveSystemPrompt);

            var cost = RequestCost.Compute(
                promptLength: RequestCost.ApproximateTokens(formatted),
                expectedOutputLength: RequestCost.EstimateOutputLength(userMessage),
                isFirstTurn: true,
                _schedulingOptions);

            var newState = new ConversationState
            {
                ConnectionId = connectionId,
                Conversation = null,
                SystemPrompt = effectiveSystemPrompt,   // ← همیشه مقدار داشته باشد
                IsCompleted = false,
                IsWaitingForPrompt = false,
                GeneratedTokens = 0,
                LastActivity = DateTime.UtcNow,
                TokenChannel = Channel.CreateUnbounded<string>(),
                PromptSubmittedAt = DateTime.UtcNow,
                Cost = cost,
                TurnCount = 1,
                TotalGeneratedTokens = 0,

                // ⬅️ [PREFIX SHARING]
                RawUserMessage = userMessage
            };

            _adaptiveTelemetry.RecordArrival(connectionId, RequestCost.ApproximateTokens(formatted));

            if (_schedulingState.Enabled || _adaptiveControllerState.IsActuating)
            {
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
                var conv = _executor.Create();
                conv.Prompt(formatted);

                newState.Conversation = conv;
                newState.IsAdmitted = true;
                newState.AdmittedAt = DateTimeOffset.UtcNow;
                _adaptiveTelemetry.RecordAdmitted(connectionId, newState.AdmittedAt, GetEffectiveAdmissionTarget());

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

            state.TotalGeneratedTokens += state.GeneratedTokens;
            state.TurnCount++;

            state.IsCompleted = false;
            state.IsWaitingForPrompt = false;
            state.GeneratedTokens = 0;
            state.LastActivity = DateTime.UtcNow;
            state.PromptSubmittedAt = DateTime.UtcNow;
            state.FirstTokenAt = null;
            state.LastTokenAt = null;
            state.ErrorMessage = null;
            _decoders.TryRemove(connectionId, out _);

            // ⬅️ [PREFIX SHARING] برای نوبت‌های بعدی، raw message را هم به‌روز کن
            state.RawUserMessage = userMessage;

            var formatted = GemmaPromptFormatter.FormatFollowUpTurn(userMessage);

            var cost = RequestCost.Compute(
                promptLength: RequestCost.ApproximateTokens(formatted),
                expectedOutputLength: RequestCost.EstimateOutputLength(userMessage),
                isFirstTurn: false,
                _schedulingOptions);

            state.Cost = cost;
            _adaptiveTelemetry.RecordArrival(connectionId, RequestCost.ApproximateTokens(formatted));

            if (_schedulingState.Enabled || _adaptiveControllerState.IsActuating)
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
                _adaptiveTelemetry.RecordAdmitted(connectionId, state.AdmittedAt, GetEffectiveAdmissionTarget());
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
            _adaptiveTelemetry.ForgetPending(connectionId);
            _scheduler.Remove(connectionId);
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

        // ⬅️ [PREFIX SHARING] warmup قبل از شروع حلقه
        if (_prefixSharingState.Enabled)
        {
            await WarmupPrefixCacheAsync(stoppingToken);
        }

        await RunInferenceLoopAsync(stoppingToken);
    }

    // =====================================================
    // ⬅️ [PREFIX SHARING] Warmup: prefill System Prompt یک‌بار
    // =====================================================
    // =====================================================
    // ⬅️ [PREFIX SHARING] Warmup: prefill System Prompt یکبار
    // =====================================================
    private async Task WarmupPrefixCacheAsync(CancellationToken stoppingToken)
    {
        try
        {
            var systemPrompt = _prefixSharingOptions.DefaultSystemPromptOverride
                ?? DefaultSystemPrompt;

            var prefixText = BuildSharedPrefix(systemPrompt);
            var prefixTokens = RequestCost.ApproximateTokens(prefixText);

            if (prefixTokens < _prefixSharingOptions.MinSystemPromptTokens)
            {
                _logger.LogInformation(
                    "⏭️ PrefixCache warmup رد شد: System Prompt کوتاه ({T} < {Min})",
                    prefixTokens, _prefixSharingOptions.MinSystemPromptTokens);
                return;
            }

            _logger.LogInformation(
                "🔥 PrefixCache warmup شروع: prefill {T} توکن System Prompt",
                prefixTokens);

            await _executorLock.WaitAsync(stoppingToken);
            try
            {
                var template = _executor.Create();
                template.Prompt(prefixText);

                var result = await _executor.Infer(stoppingToken);
                if (result != DecodeResult.Ok)
                {
                    _logger.LogWarning(
                        "⚠️ PrefixCache warmup ناموفق: {Result}", result);
                    template.Dispose();
                    return;
                }

                // ⬅️ [FIX] SaveState فقط با filePath کار می‌کند
                var tempFile = Path.Combine(Path.GetTempPath(),
                    $"prefixcache_warmup_{Guid.NewGuid():N}.bin");

                try
                {
                    _executor.Context.SaveState(tempFile);
                    var stateBytes = await File.ReadAllBytesAsync(tempFile, stoppingToken);
                    _prefixCache.Store(systemPrompt, stateBytes, prefixTokens);

                    _logger.LogInformation(
                        "✅ PrefixCache آماده ({Size}KB)", stateBytes.Length / 1024);
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }

                // حالا template را Dispose کن
                template.Dispose();
            }
            finally
            {
                _executorLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در PrefixCache warmup");
        }
    }

    /// <summary>ساخت prefix مشترک بین همه‌ی کاربران.</summary>
    private static string BuildSharedPrefix(string systemPrompt)
        => $"<start_of_turn>user\n{systemPrompt}\n\n";

    /// <summary>ساخت suffix اختصاصی هر کاربر (بدون prefix مشترک).</summary>
    private static string BuildSharedSuffix(string userMessage)
        => $"{userMessage}<end_of_turn>\n<start_of_turn>model\n";

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
                    // مرحله ۰: مدیریت صف
                    if (_schedulingState.Enabled || _adaptiveControllerState.IsActuating)
                        ProcessPendingAdmissions();
                    else if (_scheduler.PendingCount > 0)
                        FlushPendingAdmissions();

                    // به‌روزرسانی اولویت‌ها
                    if (_kvCacheState.Enabled && !_conversations.IsEmpty)
                        _kvCachePolicy.UpdatePriorities(_conversations.Values.ToList());

                    // تغذیه‌ی قطعات
                    if (_chunkedPrefillState.Enabled)
                        FeedPrefillChunks();

                    // مرحله ۱: Infer
                    // ============================================
                    // ⬅️ [FIX] دیگر IsCompleted را چک نمی‌کنیم، تا EOG token پردازش شود
                    bool anyRequiresInference = _conversations.Values
                        .Any(c => c.Conversation != null
                               && c.Conversation.RequiresInference);

                    if (anyRequiresInference)
                    {
                        var result = await _executor.Infer(stoppingToken);

                        if (result == DecodeResult.NoKvSlot)
                        {
                            _logger.LogWarning("⚠️ KV cache پر شده است.");
                            _metrics.RecordNoKvSlot();

                            bool slotFreed = _kvCacheState.Enabled
                                ? HandleNoKvSlotWithPolicy()
                                : FreeCompletedForFallback();

                            if (!slotFreed)
                                await Task.Delay(200, stoppingToken);

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

                    // مرحله ۲: Sample
                    foreach (var kvp in _conversations.ToArray())
                    {
                        var connectionId = kvp.Key;
                        var state = kvp.Value;

                        if (!state.IsAdmitted) continue;
                        if (state.Conversation == null) continue;
                        if (state.IsCompleted) continue;
                        if (!state.Conversation.RequiresSampling) continue;
                        if (state.IsPrefilling) continue;

                        var token = state.Conversation.Sample(sampler);
                        var now = DateTime.UtcNow;

                        if (state.FirstTokenAt == null && state.PromptSubmittedAt.HasValue)
                        {
                            state.FirstTokenAt = now;
                            var ttftMs = (now - state.PromptSubmittedAt.Value).TotalMilliseconds;
                            _metrics.RecordTtft(ttftMs);
                        }

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
                            RecordAdaptiveCompletion(state);
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

                // تله‌متری
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
    // ⬅️ [SCHEDULING] مدیریت صف
    // =====================================================

    private void ProcessPendingAdmissions()
    {
        if (_scheduler.PendingCount == 0) return;

        FreeAllCompletedSlots();

        var activeCount = _conversations.Values.Count(
            c => c.IsAdmitted && c.Conversation != null);

        var configuredMaxActive = _schedulingOptions.MaxActiveConversations > 0
            ? Math.Min(_schedulingOptions.MaxActiveConversations, (int)SeqMax)
            : (int)SeqMax;
        var maxActive = _adaptiveControllerState.IsActuating
            ? Math.Min(configuredMaxActive, _adaptiveControllerState.TargetConcurrency)
            : configuredMaxActive;

        var availableSlots = maxActive - activeCount;
        if (availableSlots <= 0) return;

        var admitted = _scheduler.DequeueAdmittable(
            availableSlots,
            _schedulingOptions.MaxHeavyPrefillsPerStep);

        foreach (var state in admitted)
            AdmitState(state, "queue");
    }

    private void FreeAllCompletedSlots()
    {
        var completed = _conversations.Values
            .Where(c => c.IsCompleted && c.Conversation != null)
            .ToList();

        foreach (var c in completed)
        {
            _logger.LogInformation(
                "♻️ آزادسازی فوری slot مکالمه‌ی تمام‌شده {ConnectionId}",
                c.ConnectionId);
            RemoveConversation(c.ConnectionId);
        }
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
    // =====================================================
    // ⬅️ [NEW] تشخیص پیام کوتاه (که Prefix Sharing برایش مناسب نیست)
    // =====================================================
    /// <summary>
    /// بررسی می‌کند آیا پیام کاربر به‌قدری بلند هست که Prefix Sharing
    /// از دست رفتن زبان را جبران کند.
    /// 
    /// قاعده: پیام باید هم حداقل تعداد کاراکتر و هم حداقل تعداد کلمات را داشته باشد.
    /// </summary>
    private bool IsMessageLongEnoughForPrefix(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var length = userMessage.Trim().Length;
        if (length < _prefixSharingOptions.MinUserMessageLengthForPrefix)
            return false;

        var words = userMessage
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Length;

        if (words < _prefixSharingOptions.MinUserMessageWordsForPrefix)
            return false;

        return true;
    }
    // =====================================================
    // ⬅️ [ADMISSION] با ادغام هر سه راهکار
    // =====================================================
    private void AdmitState(ConversationState state, string reason)
{
    try
    {
        // ⬅️ [FIX] محافظت: اگر RequiresInference، در صف بمان
        if (state.Conversation != null && state.Conversation.RequiresInference)
        {
            _logger.LogDebug(
                "⏸️ {ConnectionId}: conversation هنوز Infer نشده؛ به صف برمی‌گردد",
                state.ConnectionId);

            state.IsAdmitted = false;
            state.EnqueuedAt = DateTimeOffset.UtcNow;
            _scheduler.Enqueue(state);
            return;
        }

        bool conversationCreated = false;
        if (state.Conversation == null)
        {
            state.Conversation = _executor.Create();
            conversationCreated = true;
        }

        bool usedPrefixCache = false;
        bool usedChunked = false;

            // === راهکار ۳: PrefixCache (فقط نوبت اول) ===
            // === راهکار ۳: PrefixCache (فقط نوبت اول + پیام بلند) ===
            bool shouldUsePrefixCache =
                _prefixSharingState.Enabled
                && conversationCreated
                && state.TurnCount == 1
                && !string.IsNullOrEmpty(state.RawUserMessage)
                && IsMessageLongEnoughForPrefix(state.RawUserMessage);

            if (shouldUsePrefixCache)
            {
                var systemPrompt = state.SystemPrompt ?? DefaultSystemPrompt;

                if (_prefixCache.TryGetState(systemPrompt, out var cachedState)
                    && cachedState != null)
                {
                    var tempFile = Path.Combine(Path.GetTempPath(),
                        $"prefixcache_load_{Guid.NewGuid():N}.bin");

                    try
                    {
                        File.WriteAllBytes(tempFile, cachedState);
                        _executor.Context.LoadState(tempFile);

                        var newConv = _executor.Create();
                        var suffix = BuildSharedSuffix(state.RawUserMessage!);
                        newConv.Prompt(suffix);

                        state.Conversation.Dispose();
                        state.Conversation = newConv;
                        usedPrefixCache = true;
                        _prefixSharingState.RecordHit();

                        _logger.LogInformation(
                            "♻️ PrefixCache HIT برای {ConnectionId} ({T} توکن صرفه‌جویی, msg={M}chars)",
                            state.ConnectionId,
                            _prefixCache.GetPromptTokens(systemPrompt),
                            state.RawUserMessage!.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "خطا در PrefixCache برای {ConnectionId}؛ fallback",
                            state.ConnectionId);
                    }
                    finally
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }

                if (!usedPrefixCache)
                    _prefixSharingState.RecordMiss();
            }
            else if (_prefixSharingState.Enabled && state.TurnCount == 1)
            {
                // ⬅️ [NEW] لاگ برای رد کردن آگاهانه
                _logger.LogDebug(
                    "⏭️ PrefixCache رد شد برای {ConnectionId}: پیام کوتاه ({L} chars < {Min})",
                    state.ConnectionId,
                    state.RawUserMessage?.Length ?? 0,
                    _prefixSharingOptions.MinUserMessageLengthForPrefix);
            }

            // === fallback: Chunked Prefill یا مسیر عادی ===
            if (!usedPrefixCache)
        {
            var prompt = state.PendingPrompt!;

            var useChunked = _chunkedPrefillState.Enabled
                && prompt.Length >= _chunkedPrefillOptions.MinLengthToChunk
                && (!_chunkedPrefillOptions.OnlyFirstTurn || state.TurnCount <= 1);

            if (useChunked)
            {
                var chunks = PrefillChunker.Split(
                    prompt, _chunkedPrefillOptions.ChunkSizeCharacters);

                state.Conversation.Prompt(chunks[0]);
                state.PrefillChunks = new Queue<string>(chunks.Skip(1));
                state.IsPrefilling = state.PrefillChunks.Count > 0;
                usedChunked = true;

                _logger.LogInformation(
                    "🧩 {ConnectionId} Chunked Prefill: {N} قطعه",
                    state.ConnectionId, chunks.Count);
            }
            else
            {
                state.Conversation.Prompt(prompt);
                state.PrefillChunks = null;
                state.IsPrefilling = false;
            }
        }

        state.IsAdmitted = true;
        state.AdmittedAt = DateTimeOffset.UtcNow;
        _adaptiveTelemetry.RecordAdmitted(state.ConnectionId, state.AdmittedAt, GetEffectiveAdmissionTarget());
        state.PendingPrompt = null;
        state.PromptSubmittedAt = DateTime.UtcNow;

        var waited = state.EnqueuedAt is { } t
            ? (DateTimeOffset.UtcNow - t).TotalMilliseconds
            : 0;

        _logger.LogInformation(
            "✅ {ConnectionId} پذیرفته شد (reason={R}, waited={W:F0}ms, prefix={PC}, chunked={CH})",
            state.ConnectionId, reason, waited, usedPrefixCache, usedChunked);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "خطا در admission {ConnectionId}", state.ConnectionId);
        _adaptiveTelemetry.RecordFailure(state.ConnectionId);
        state.ErrorMessage = ex.Message;
        state.IsCompleted = true;
        state.IsAdmitted = false;
        state.TokenChannel.Writer.TryComplete();
    }
}

    // =====================================================
    // ⬅️ [KV CACHE POLICY] مدیریت NoKvSlot
    // =====================================================

    private bool HandleNoKvSlotWithPolicy()
    {
        var snapshot = _conversations.Values.ToList();
        if (snapshot.Count == 0) return false;

        var victim = _kvCachePolicy.SelectVictim(snapshot, excludeConnectionId: null);

        if (victim == null)
        {
            _logger.LogWarning(
                "⏳ NoKvSlot: هیچ قربانی مناسبی نبود. ۲۰۰ms صبر می‌کنیم.");
            return false;
        }

        _adaptiveTelemetry.RecordFailure(victim.ConnectionId);
        victim.WasEvicted = true;
        victim.EvictionReason = "kv_cache_pressure";
        victim.ErrorMessage = "پاسخ شما به دلیل فشار حافظه قطع شد.";
        victim.IsCompleted = true;
        victim.TokenChannel.Writer.TryComplete();
        RemoveConversation(victim.ConnectionId);

        _logger.LogWarning(
            "🗑️ KV Cache: قربانی {ConnectionId} حذف شد (priority={P:F2})",
            victim.ConnectionId, victim.Priority);

        return true;
    }

    /// <summary>Fallback زمانی که سیاست KV غیرفعال است.</summary>
    private bool FreeCompletedForFallback()
    {
        var before = _conversations.Count;
        FreeAllCompletedSlots();
        if (_conversations.Count < before) return true;

        CleanupInactive(TimeSpan.FromMinutes(5));
        return _conversations.Count < before;
    }

    // =====================================================
    // ⬅️ [CHUNKED PREFILL] تغذیه‌ی قطعات
    // =====================================================
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
    // کمکی‌ها
    // =====================================================
    private int GetEffectiveAdmissionTarget()
    {
        var configuredMax = _schedulingOptions.MaxActiveConversations > 0
            ? Math.Min(_schedulingOptions.MaxActiveConversations, (int)SeqMax)
            : (int)SeqMax;
        return _adaptiveControllerState.IsActuating
            ? Math.Min(configuredMax, _adaptiveControllerState.TargetConcurrency)
            : configuredMax;
    }

    private void RecordAdaptiveCompletion(ConversationState state)
    {
        try
        {
            _adaptiveTelemetry.RecordCompletion(
                state.ConnectionId,
                state.FirstTokenAt,
                state.LastTokenAt,
                state.GeneratedTokens,
                GetActiveSequenceCount(),
                GetQueuedCount(),
                GetEffectiveAdmissionTarget());
        }
        catch (Exception ex)
        {
            // Adaptive telemetry must never interrupt inference or token delivery.
            _logger.LogDebug(ex, "ثبت متریک کنترل‌گر تطبیقی برای {ConnectionId} ناموفق بود.", state.ConnectionId);
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