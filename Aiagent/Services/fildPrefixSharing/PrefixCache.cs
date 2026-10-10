using LLama.Batched;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aiagent.Services.Scheduling;

/// <summary>
/// کش KV برای System Prompt های مشترک.
/// 
/// این کلاس در startup یک‌بار System Prompt پیش‌فرض را prefill می‌کند
/// و state آن را در حافظه نگه می‌دارد. هر مکالمه‌ی جدیدی که با همان
/// System Prompt شروع شود، از این state بارگذاری می‌کند و فقط باید
/// user_message را از صفر prefill کند.
/// </summary>
public sealed class PrefixCache : IDisposable
{
    private readonly ILogger<PrefixCache> _logger;
    private readonly PrefixSharingOptions _options;

    // key = hash(systemPrompt) → value = state bytes
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly ConcurrentDictionary<string, int> _promptTokens = new();

    public PrefixCache(
        ILogger<PrefixCache> logger,
        Microsoft.Extensions.Options.IOptions<PrefixSharingOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool HasCache(string systemPrompt)
        => _cache.ContainsKey(HashKey(systemPrompt));

    /// <summary>
    /// درخواست prefill یک System Prompt جدید را برمی‌گرداند.
    /// اگر در cache باشد، false برمی‌گرداند (نیازی به prefill نیست).
    /// </summary>
    public bool TryGetState(string systemPrompt, out byte[]? state)
    {
        var hit = _cache.TryGetValue(HashKey(systemPrompt), out state);
        return hit;
    }

    /// <summary>
    /// ذخیره‌ی state یک System Prompt که تازه prefill شده.
    /// </summary>
    public void Store(string systemPrompt, byte[] state, int promptTokenCount)
    {
        var key = HashKey(systemPrompt);
        _cache[key] = state;
        _promptTokens[key] = promptTokenCount;

        _logger.LogInformation(
            "📦 PrefixCache: ذخیره شد (hash={Hash}, tokens={Tokens}, size={Size}KB)",
            key[..8], promptTokenCount, state.Length / 1024);
    }

    public int GetPromptTokens(string systemPrompt)
        => _promptTokens.TryGetValue(HashKey(systemPrompt), out var t) ? t : 0;

    public IEnumerable<string> GetCachedHashes() => _cache.Keys;

    private static string HashKey(string systemPrompt)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(systemPrompt ?? "");
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _cache.Clear();
}