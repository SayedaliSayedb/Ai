//using Aiagent.Models;
//using LLama;
//using LLama.Batched;
//using LLama.Common;
//using LLama.Native;
//using LLama.Sampling;
//using Microsoft.Extensions.Logging;
//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Runtime.CompilerServices;
//using System.Text;
//using System.Text.RegularExpressions;
//using System.Threading;
//using System.Threading.Tasks;

//namespace Aiagent.Services;

//public class TextGenerator : IDisposable
//{
//    private readonly ILogger<TextGenerator> _logger;
//    private readonly LLamaWeights _model;
//    private readonly BatchedExecutor _executor;
//    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
//    private bool _disposed;

//    private const string DefaultSystemPrompt =
//        "You are a helpful AI assistant. " +
//        "Respond in the same language as the user's question. " +
//        "ONLY when the user explicitly asks for code, wrap your answer " +
//        "in a fenced code block. For normal greetings, questions, or explanations, " +
//        "DO NOT use code blocks. Reply with plain text in those cases. " +
//        "When you do write code, use this format: " +
//        "three backticks, then the language name, then a newline, " +
//        "then the code, then a newline, then three closing backticks. " +
//        "Do not translate or repeat the user's request. Answer directly.";

//    public TextGenerator(ILogger<TextGenerator> logger)
//    {
//        _logger = logger;
//        var modelPath = "models/gemma-3-4b-it-Q4_K_M.gguf";

//        if (!File.Exists(modelPath))
//        {
//            _logger.LogError("❌ فایل مدل در مسیر {ModelPath} یافت نشد!", modelPath);
//            throw new FileNotFoundException($"Model file not found: {modelPath}");
//        }

//        try
//        {
//            _logger.LogInformation("📂 در حال بارگذاری مدل از مسیر: {ModelPath}", modelPath);

//            var parameters = new ModelParams(modelPath)
//            {
//                ContextSize = 4096,
//                GpuLayerCount = 20,
//                BatchSize = 512,
//                SeqMax = 16,
//                Threads = Math.Max(1, Environment.ProcessorCount)
//            };

//            _model = LLamaWeights.LoadFromFile(parameters);
//            _executor = new BatchedExecutor(_model, parameters);

//            _logger.LogInformation("✅ مدل و BatchedExecutor با موفقیت بارگذاری شدند.");
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "❌ خطا در بارگذاری مدل از مسیر {ModelPath}", modelPath);
//            throw;
//        }
//    }

//    // =====================================================
//    // متد غیراستریم — برای TextAnalysisWorker
//    // =====================================================
//    public async Task<string> GenerateResponseAsync(
//        string userPrompt,
//        string? systemPrompt = null)
//    {
//        if (_disposed)
//            throw new ObjectDisposedException(nameof(TextGenerator));

//        var connectionId = $"task-{Guid.NewGuid():N}";
//        var result = new StringBuilder();

//        try
//        {
//            await foreach (var chunk in GenerateStreamResponseAsync(
//                connectionId,
//                userPrompt,
//                systemPrompt,
//                CancellationToken.None))
//            {
//                result.Append(chunk);
//            }

//            return result.ToString().Trim();
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "خطا در GenerateResponseAsync");
//            return $"❌ خطا: {ex.Message}";
//        }
//        finally
//        {
//            RemoveConversation(connectionId);
//        }
//    }

//    // =====================================================
//    // دریافت یا ایجاد ConversationState
//    // =====================================================
//    public Task<ConversationState> GetOrCreateConversationStateAsync(
//        string connectionId,
//        string? systemPrompt = null)
//    {
//        if (_conversations.TryGetValue(connectionId, out var state))
//        {
//            state.LastActivity = DateTime.UtcNow;
//            return Task.FromResult(state);
//        }

//        var conversation = _executor.Create();
//        var prompt = !string.IsNullOrWhiteSpace(systemPrompt) ? systemPrompt : DefaultSystemPrompt;

//        // System Prompt را وارد مکالمه می‌کنیم و بلافاصله Infer می‌کنیم
//        conversation.Prompt(prompt);

//        var newState = new ConversationState
//        {
//            Conversation = conversation,
//            LastActivity = DateTime.UtcNow,
//            IsProcessing = false
//        };

//        _conversations.TryAdd(connectionId, newState);
//        _logger.LogInformation("مکالمه جدید برای ConnectionId={ConnectionId} ایجاد شد.", connectionId);

//        return Task.FromResult(newState);
//    }

//    // =====================================================
//    // حذف مکالمه
//    // =====================================================
//    public void RemoveConversation(string connectionId)
//    {
//        if (_conversations.TryRemove(connectionId, out var state))
//        {
//            try { state.Conversation.Dispose(); } catch { }
//            try { state.Lock.Dispose(); } catch { }
//            _logger.LogInformation("مکالمه ConnectionId={ConnectionId} حذف شد.", connectionId);
//        }
//    }

//    // =====================================================
//    // ✅✅✅ متد اصلی استریم — اصلاح‌شده
//    // =====================================================
//    public async IAsyncEnumerable<string> GenerateStreamResponseAsync(
//        string connectionId,
//        string userMessage,
//        string? systemPrompt = null,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        if (_disposed)
//            throw new ObjectDisposedException(nameof(TextGenerator));

//        var state = await GetOrCreateConversationStateAsync(connectionId, systemPrompt);
//        var conversation = state.Conversation;

//        await state.Lock.WaitAsync(cancellationToken);

//        var sampler = new DefaultSamplingPipeline
//        {
//            Temperature = 0.7f,
//            TopP = 0.95f,
//            RepeatPenalty = 1.05f
//        };

//        var decoder = new StreamingTokenDecoder(_executor.Context)
//        {
//            DecodeSpecialTokens = false
//        };

//        var vocab = _executor.Context.NativeHandle.ModelHandle.Vocab;

//        try
//        {
//            // ==================================================
//            // ✅✅✅ راه‌حل اصلی مشکل ✅✅✅
//            //
//            // قبل از Prompt() جدید، بررسی می‌کنیم که آیا مکالمه
//            // در حالت "Waiting for Inference" است یا خیر.
//            //
//            // اگر RequiresInference == true باشد، یعنی Prompt()
//            // قبلی هنوز پردازش نشده و باید ابتدا Infer() بزنیم.
//            // ==================================================
//            while (conversation.RequiresInference)
//            {
//                _logger.LogDebug(
//                    "Conversation {ConnectionId} requires inference before new prompt. Running Infer()...",
//                    connectionId);

//                var result = await _executor.Infer(cancellationToken);

//                if (result != DecodeResult.Ok)
//                {
//                    _logger.LogWarning("Infer() returned {Result} for ConnectionId={ConnectionId}", result, connectionId);
//                    break;
//                }
//            }

//            // ==================================================
//            // حالا مکالمه آماده‌ی Prompt() جدید است
//            // ==================================================
//            conversation.Prompt(userMessage);

//            bool done = false;

//            while (!done)
//            {
//                cancellationToken.ThrowIfCancellationRequested();

//                // اجرای یک مرحله استنتاج
//                var decodeResult = await _executor.Infer(cancellationToken);

//                if (decodeResult == DecodeResult.NoKvSlot)
//                {
//                    _logger.LogWarning("KV cache space exhausted for ConnectionId={ConnectionId}", connectionId);
//                    yield return "❌ فضای حافظه KV cache تکمیل شده است.";
//                    done = true;
//                    continue;
//                }

//                if (decodeResult != DecodeResult.Ok)
//                {
//                    _logger.LogError("Decode failed with result: {Result}", decodeResult);
//                    yield return $"❌ خطا در پردازش: {decodeResult}";
//                    done = true;
//                    continue;
//                }

//                if (!conversation.RequiresSampling)
//                {
//                    await Task.Delay(1, cancellationToken);
//                    continue;
//                }

//                var token = conversation.Sample(sampler);

//                // ✅ توکن پایان تولید را از صف خارج می‌کنیم
//                if (token.IsEndOfGeneration(vocab))
//                {
//                    await _executor.Infer(cancellationToken);
//                    done = true;
//                    continue;
//                }

//                decoder.Add(token);
//                var text = CleanChunk(decoder.Read());

//                if (!string.IsNullOrEmpty(text))
//                {
//                    yield return text;
//                }
//            }
//        }
//        finally
//        {
//            state.Lock.Release();
//        }
//    }

//    private static string CleanChunk(string chunk)
//    {
//        if (string.IsNullOrEmpty(chunk))
//            return string.Empty;

//        chunk = Regex.Replace(
//            chunk,
//            @"<0x([0-9A-Fa-f]{2})>",
//            match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()
//        );

//        var patterns = new[] { "Bot:", "User:", "Assistant:", "User >" };
//        foreach (var pattern in patterns)
//        {
//            if (chunk.Contains(pattern, StringComparison.OrdinalIgnoreCase))
//                chunk = chunk.Replace(pattern, string.Empty, StringComparison.OrdinalIgnoreCase);
//        }

//        return chunk;
//    }

//    public void CleanupInactiveConversations(TimeSpan timeout)
//    {
//        var now = DateTime.UtcNow;
//        var inactive = _conversations
//            .Where(kvp => now - kvp.Value.LastActivity > timeout)
//            .Select(kvp => kvp.Key)
//            .ToList();

//        foreach (var key in inactive)
//        {
//            RemoveConversation(key);
//        }
//    }

//    public void Dispose()
//    {
//        if (_disposed) return;
//        _disposed = true;

//        foreach (var state in _conversations.Values)
//        {
//            try { state.Conversation.Dispose(); } catch { }
//            try { state.Lock.Dispose(); } catch { }
//        }
//        _conversations.Clear();

//        try { _executor?.Dispose(); } catch { }
//        try { _model?.Dispose(); } catch { }
//    }
//}