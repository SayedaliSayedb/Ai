namespace Aiagent.Services.Scheduling;

/// <summary>
/// بردار هزینه‌ی یک درخواست که برای اولویت‌بندی در صف استفاده می‌شود.
/// </summary>
public sealed class RequestCost
{
    /// <summary>مقدار پیش‌فرض — برای مکالمه‌ی بدون هزینه (مثلاً در مسیر Baseline).</summary>
    public static readonly RequestCost Empty = new()
    {
        PromptLength = 0,
        ExpectedOutputLength = 0,
        IsFirstTurn = false,
        Score = 0,
        IsHeavy = false,
        IsFast = true
    };

    public required int PromptLength { get; init; }
    public required int ExpectedOutputLength { get; init; }
    public required bool IsFirstTurn { get; init; }

    /// <summary>امتیاز نهایی — مقدار کمتر = سریع‌تر.</summary>
    public required double Score { get; init; }

    /// <summary>آیا این درخواست Prefill سنگین دارد؟</summary>
    public required bool IsHeavy { get; init; }

    /// <summary>آیا این درخواست در صف سریع قرار می‌گیرد؟</summary>
    public required bool IsFast { get; init; }

    public static RequestCost Compute(
        int promptLength,
        int expectedOutputLength,
        bool isFirstTurn,
        SchedulingOptions opts)
    {
        var score = opts.Alpha * promptLength
                  + opts.Beta * expectedOutputLength
                  + (isFirstTurn ? opts.Gamma : 0.0);

        var isHeavy = isFirstTurn && promptLength >= opts.HeavyPrefillPromptThreshold;

        return new RequestCost
        {
            PromptLength = promptLength,
            ExpectedOutputLength = expectedOutputLength,
            IsFirstTurn = isFirstTurn,
            Score = score,
            IsHeavy = isHeavy,
            IsFast = score < opts.FastQueueThreshold
        };
    }

    /// <summary>تخمین تعداد توکن بر اساس طول متن (ترکیب فارسی/انگلیسی).</summary>
    public static int ApproximateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, (int)(text.Length / 3.5));
    }

    /// <summary>تخمین طول خروجی بر اساس محتوای پیام کاربر.</summary>
    public static int EstimateOutputLength(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return 100;

        var lower = userMessage.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("کد")
            || lower.Contains("function") || lower.Contains("تابع")
            || lower.Contains("class") || lower.Contains("کلاس"))
            return 500;

        if (lower.Contains("ترجمه") || lower.Contains("translate")
            || lower.Contains("خلاصه") || lower.Contains("summarize"))
            return 250;

        if (userMessage.Length < 30) return 80;

        return 200;
    }
}