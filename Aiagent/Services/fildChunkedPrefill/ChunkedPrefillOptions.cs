namespace Aiagent.Services.Scheduling;

/// <summary>
/// تنظیمات راهکار ۴: پیش‌پردازش قطعه‌ای.
/// </summary>
public sealed class ChunkedPrefillOptions
{
    public const string SectionName = "ChunkedPrefill";

    /// <summary>وضعیت اولیه هنگام راه‌اندازی.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>امکان تغییر در زمان اجرا.</summary>
    public bool AllowRuntimeToggle { get; set; } = true;

    /// <summary>اندازه‌ی هر قطعه بر حسب کاراکتر (تخمین ~۳.۵ کاراکتر/توکن).</summary>
    public int ChunkSizeCharacters { get; set; } = 448;   // ≈ 128 توکن

    /// <summary>حداکثر تعداد قطعه‌ای که در هر گام مرکزی Prefill می‌شود.</summary>
    public int MaxChunksPerStep { get; set; } = 1;

    /// <summary>حداقل طول پرامپت (کاراکتر) که chunked prefill را فعال می‌کند.</summary>
    public int MinLengthToChunk { get; set; } = 600;

    /// <summary>اگر true، فقط نوبت اول chunk می‌شود؛ پیام‌های بعدی معمولی.</summary>
    public bool OnlyFirstTurn { get; set; } = false;
}