namespace Aiagent.Services.Scheduling;

/// <summary>
/// تنظیمات راهکار ۲: مدیریت اولویت‌محور KV Cache.
/// </summary>
public sealed class KvCachePolicyOptions
{
    public const string SectionName = "KvCachePolicy";

    /// <summary>وضعیت اولیه هنگام راه‌اندازی.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>امکان تغییر وضعیت در زمان اجرا از طریق API.</summary>
    public bool AllowRuntimeToggle { get; set; } = true;

    // ===== ضرایب فرمول اولویت =====
    /// <summary>وزن فعالیت اخیر (۰ تا ۱).</summary>
    public double W1_RecentActivity { get; set; } = 3.0;

    /// <summary>وزن «در حال استریم بودن».</summary>
    public double W2_IsStreaming { get; set; } = 1000.0;   // عملاً وتوی مطلق

    /// <summary>وزن طول مکالمه.</summary>
    public double W3_ConversationLength { get; set; } = 1.0;

    /// <summary>وزن هزینه‌ی بازسازی (منفی).</summary>
    public double W4_RebuildCost { get; set; } = 2.0;

    // ===== پارامترهای کمکی =====
    /// <summary>پنجره‌ی زمانی برای نرمال‌سازی recent_activity (ثانیه).</summary>
    public int ActivityWindowSeconds { get; set; } = 120;

    /// <summary>حداکثر تعداد نوبت برای نرمال‌سازی conversation_length.</summary>
    public int MaxTurnsForNormalization { get; set; } = 20;

    /// <summary>حداقل اولویت برای اینکه یک مکالمه قربانی شود. زیر این مقدار = قربانی مجاز.</summary>
    public double MinPriorityToKeep { get; set; } = 0.0;

    /// <summary>آستانه‌ی Activity که مکالمه‌ی تمام‌شده را «غیرفعال» می‌داند (ثانیه).</summary>
    public int CompletedIdleThresholdSeconds { get; set; } = 30;
}