using Aiagent.Models;

namespace Aiagent.Services.Metrics;

public interface IThresholdService
{
    Task<MetricsThreshold> GetAsync();
    Task SaveAsync(MetricsThreshold threshold);

    /// <summary>
    /// بررسی یک نمونه‌ی متریک و ثبت رکورد نقض در صورت تجاوز از آستانه.
    /// </summary>
    IReadOnlyList<ThresholdBreachRecord> Evaluate(MetricSample sample);

    /// <summary>
    /// خواندن تمام رکوردهای نقض (برای نمایش در داشبورد).
    /// </summary>
    Task<IReadOnlyList<ThresholdBreachRecord>> GetRecentBreachesAsync(int count = 100);

    event Action<ThresholdBreachRecord>? OnBreach;
}