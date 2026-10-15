using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Aiagent.Services.Adaptive;

/// <summary>
/// Reads basic GPU memory/utilization data through nvidia-smi. If unavailable,
/// the controller continues without GPU telemetry and must not pretend values are zero.
/// </summary>
public sealed class NvidiaSmiGpuTelemetryProvider : IGpuTelemetryProvider
{
    private readonly AdaptiveControllerOptions _options;
    private readonly ILogger<NvidiaSmiGpuTelemetryProvider> _logger;

    public NvidiaSmiGpuTelemetryProvider(
        IOptions<AdaptiveControllerOptions> options,
        ILogger<NvidiaSmiGpuTelemetryProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GpuTelemetrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(_options.NvidiaSmiPath) ? "nvidia-smi" : _options.NvidiaSmiPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add($"--id={Math.Max(0, _options.GpuIndex)}");
        process.StartInfo.ArgumentList.Add("--query-gpu=memory.used,memory.total,utilization.gpu");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        try
        {
            if (!process.Start())
                return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex, "nvidia-smi process did not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex,
                    string.IsNullOrWhiteSpace(stderr) ? $"nvidia-smi exit code {process.ExitCode}" : stderr.Trim());

            var line = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is null) return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex, "nvidia-smi returned no data.");

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var util) || total <= 0)
            {
                return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex, $"Could not parse nvidia-smi output: {line}");
            }

            return new GpuTelemetrySnapshot
            {
                Available = true,
                GpuIndex = _options.GpuIndex,
                MemoryUsedMiB = used,
                MemoryTotalMiB = total,
                MemoryUsedRatio = Math.Clamp((double)used / total, 0, 1),
                UtilizationPercent = Math.Clamp(util, 0, 100),
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex, "nvidia-smi timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            _logger.LogDebug(ex, "GPU telemetry via nvidia-smi is unavailable.");
            return GpuTelemetrySnapshot.Unavailable(_options.GpuIndex, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort cleanup */ }
    }
}
