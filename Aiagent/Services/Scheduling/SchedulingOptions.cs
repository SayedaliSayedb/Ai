namespace Aiagent.Services.Scheduling;

/// <summary>
/// تنظیمات راهکار ۱: صف‌بندی آگاه از تأخیر.
/// همه‌ی مقادیر از appsettings.json قابل تنظیم هستند.
/// </summary>
public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    /// <summary>وضعیت اولیه هنگام راه‌اندازی.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>امکان تغییر وضعیت در زمان اجرا از طریق API.</summary>
    public bool AllowRuntimeToggle { get; set; } = true;

    /// <summary>آستانه‌ی هزینه برای تفکیک صف سریع/کند.</summary>
    public double FastQueueThreshold { get; set; } = 100;

    /// <summary>ضریب طول پرامپت در محاسبه‌ی cost.</summary>
    public double Alpha { get; set; } = 1.0;

    /// <summary>ضریب طول خروجی تخمینی در محاسبه‌ی cost.</summary>
    public double Beta { get; set; } = 2.0;

    /// <summary>هزینه‌ی اضافی نوبت اول (Prefill سنگین).</summary>
    public double Gamma { get; set; } = 150.0;

    /// <summary>حداکثر Prefill سنگین در هر گام admission.</summary>
    public int MaxHeavyPrefillsPerStep { get; set; } = 1;

    /// <summary>حداکثر طول صف انتظار؛ فراتر از آن، درخواست رد می‌شود.</summary>
    public int MaxPendingQueueSize { get; set; } = 200;

    /// <summary>حداکثر زمان انتظار در صف (ثانیه). صفر = بدون محدودیت.</summary>
    public int PendingTimeoutSeconds { get; set; } = 120;

    /// <summary>سقف مکالمات فعال؛ صفر یعنی از SeqMax استفاده شود.</summary>
    public int MaxActiveConversations { get; set; } = 0;

    /// <summary>آستانه‌ی طول پرامپت (توکن) برای «Prefill سنگین».</summary>
    public int HeavyPrefillPromptThreshold { get; set; } = 512;
}