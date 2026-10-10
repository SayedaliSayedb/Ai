namespace Aiagent.Services.Scheduling;

public sealed class PrefixSharingOptions
{
    public const string SectionName = "PrefixSharing";

    /// <summary>فعال/غیرفعال کردن راهکار ۳.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>امکان تغییر در زمان اجرا.</summary>
    public bool AllowRuntimeToggle { get; set; } = true;

    /// <summary>
    /// حداقل طول System Prompt (توکن) که ارزش اشتراک‌گذاری دارد.
    /// اگر System Prompt کوتاه‌تر باشد، این مکانیزم استفاده نمی‌شود.
    /// </summary>
    public int MinSystemPromptTokens { get; set; } = 100;

    /// <summary>
    /// System Prompt پیش‌فرض که در startup prefill می‌شود.
    /// اگر خالی باشد، از DefaultSystemPrompt استفاده می‌شود.
    /// </summary>
    public string? DefaultSystemPromptOverride { get; set; }

    /// <summary>
    /// حالت debug: اگر true، فقط لاگ می‌کند که از cache استفاده شد.
    /// </summary>
    public bool LogCacheHits { get; set; } = true;
    // ⬅️ [NEW] آستانه‌ی طول پیام کاربر برای فعال شدن Prefix Sharing
    /// <summary>
    /// اگر طول پیام کاربر (کاراکتر) کمتر از این مقدار باشد،
    /// Prefix Sharing برای این درخواست غیرفعال می‌شود.
    /// 
    /// دلیل: promptهای کوتاه (تک‌کلمه‌ای) به دلیل از دست رفتن System Prompt
    /// در LoadState، زبان پاسخ را از دست می‌دهند.
    /// </summary>
    public int MinUserMessageLengthForPrefix { get; set; } = 20;

    // ⬅️ [NEW] آستانه‌ی تعداد کلمات
    /// <summary>
    /// اگر تعداد کلمات پیام کاربر کمتر از این مقدار باشد،
    /// Prefix Sharing برای این درخواست غیرفعال می‌شود.
    /// </summary>
    public int MinUserMessageWordsForPrefix { get; set; } = 3;
}