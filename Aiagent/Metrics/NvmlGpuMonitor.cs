using System.Diagnostics;
using System.Globalization;

namespace Aiagent.Services.Metrics;

/// <summary>
/// پیاده‌سازی سبک با فراخوانی nvidia-smi.
/// برای پروژه‌های تحقیقاتی کاملاً کافی است و بدون وابستگی بومی کار می‌کند.
/// </summary>
public class NvidiaSmiGpuMonitor : IGpuMonitor
{
    private readonly ILogger<NvidiaSmiGpuMonitor> _logger;
    public bool IsAvailable { get; private set; } = true;

    public NvidiaSmiGpuMonitor(ILogger<NvidiaSmiGpuMonitor> logger)
    {
        _logger = logger;
        // تست اولیه
        try { Read(); }
        catch { IsAvailable = false; _logger.LogWarning("nvidia-smi در دسترس نیست"); }
    }

    public GpuMetrics? Read()
    {
        if (!IsAvailable) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return null;

            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);

            if (string.IsNullOrWhiteSpace(output)) return null;

            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4) return null;

            return new GpuMetrics
            {
                UtilizationPercent = double.Parse(parts[0], CultureInfo.InvariantCulture),
                MemoryUsedMb = double.Parse(parts[1], CultureInfo.InvariantCulture),
                MemoryTotalMb = double.Parse(parts[2], CultureInfo.InvariantCulture),
                Temperature = double.Parse(parts[3], CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "خطا در خواندن nvidia-smi");
            return null;
        }
    }
}