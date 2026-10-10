using Aiagent.Hubs;
using Aiagent.Middleware;
using Aiagent.Services;
using Aiagent.Services.Auth;
using Aiagent.Services.LoadTesting;
using Aiagent.Services.Metrics;
using Aiagent.Services.Scheduling;   // ⬅️ [SCHEDULING]
using Aiagent.Services.Storage;
using LLama.Native;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;  // (در صورت نیاز)
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// گام ۱: خواندن تنظیمات
// ============================================================
var useCuda = builder.Configuration.GetValue("Llm:Cuda", true);
var nativeLogging = builder.Configuration.GetValue("Llm:NativeLogging", true);

Console.WriteLine($"🖥️ OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"🏗️ Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"🎮 CUDA backend requested: {useCuda}");

// ============================================================
// گام ۲: تشخیص مسیر و فایل‌های CUDA
// ============================================================
var baseDir = AppContext.BaseDirectory;
var cudaDir = Path.Combine(baseDir, "runtimes", "win-x64", "native", "cuda12");

Console.WriteLine($"📁 Base directory: {baseDir}");
Console.WriteLine($"📁 CUDA directory: {cudaDir}");
Console.WriteLine($"   Exists: {Directory.Exists(cudaDir)}");

var requiredFiles = new[]
{
    "llama.dll",
    "ggml.dll",
    "ggml-base.dll",
    "ggml-cuda.dll",
    "cudart64_12.dll",
    "cublas64_12.dll",
    "cublasLt64_12.dll",
    "mtmd.dll"
};

if (Directory.Exists(cudaDir))
{
    Console.WriteLine("📋 بررسی فایل‌های CUDA در پوشه cuda12:");
    foreach (var file in requiredFiles)
    {
        var path = Path.Combine(cudaDir, file);
        var exists = File.Exists(path);
        var size = exists ? new FileInfo(path).Length / 1024 : 0;
        Console.WriteLine($"   {(exists ? "✅" : "❌")} {file} ({size} KB)");
    }
}

Console.WriteLine("📋 کپی فایل‌های DLL به ریشه پوشه اجرایی...");

if (Directory.Exists(cudaDir))
{
    foreach (var file in requiredFiles)
    {
        var source = Path.Combine(cudaDir, file);
        var dest = Path.Combine(baseDir, file);

        if (File.Exists(source))
        {
            try
            {
                if (!File.Exists(dest) ||
                    new FileInfo(source).LastWriteTimeUtc > new FileInfo(dest).LastWriteTimeUtc)
                {
                    File.Copy(source, dest, overwrite: true);
                    Console.WriteLine($"   ✅ کپی شد: {file}");
                }
                else
                {
                    Console.WriteLine($"   ⏭️ از قبل موجود: {file}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ خطا در کپی {file}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"   ❌ منبع وجود ندارد: {source}");
        }
    }
}

// ============================================================
// گام ۳: افزودن پوشه cuda12 و ریشه به PATH
// ============================================================
var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
var pathsToAdd = new List<string>();

if (Directory.Exists(cudaDir) && !currentPath.Contains(cudaDir, StringComparison.OrdinalIgnoreCase))
    pathsToAdd.Add(cudaDir);

if (!string.IsNullOrEmpty(baseDir) && !currentPath.Contains(baseDir, StringComparison.OrdinalIgnoreCase))
    pathsToAdd.Add(baseDir);

if (pathsToAdd.Count > 0)
{
    var newPath = string.Join(";", pathsToAdd) + ";" + currentPath;
    Environment.SetEnvironmentVariable("PATH", newPath);
    Console.WriteLine($"🔧 مسیرهای زیر به PATH اضافه شدند:");
    foreach (var p in pathsToAdd)
        Console.WriteLine($"   {p}");
}

// ============================================================
// گام ۴: پیکربندی NativeLibraryConfig برای CUDA
// ============================================================
if (nativeLogging)
{
    NativeLibraryConfig.All.WithLogCallback((level, message) =>
    {
        Console.WriteLine($"[llama.cpp] {message}");
    });
}

var cudaLlamaPath = Path.Combine(
    baseDir, "runtimes", "win-x64", "native", "cuda12", "llama.dll");

Console.WriteLine($"📂 مسیر کتابخانه CUDA: {cudaLlamaPath}");
Console.WriteLine($"   وجود دارد: {File.Exists(cudaLlamaPath)}");

NativeLibraryConfig.LLama
    .WithLibrary(cudaLlamaPath)
    .WithCuda(true)
    .WithVulkan(false)
    .WithAutoFallback(false);

NativeApi.llama_empty_call();
Console.WriteLine("✅ NativeLibraryConfig پیکربندی شد.");

// ============================================================
// گام ۵: سرویس‌های برنامه
// ============================================================
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ----- Auth + Storage -----
builder.Services.AddSingleton<IStorageService, LocalStorageService>();
builder.Services.AddSingleton<IAuthService, JsonAuthService>();
builder.Services.AddSingleton<IThresholdService, JsonThresholdService>();
// ----- Metrics -----
builder.Services.AddSingleton<IGpuMonitor, NvidiaSmiGpuMonitor>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<IMetricsCollector>(sp =>
    sp.GetRequiredService<MetricsCollector>());
builder.Services.AddHostedService<MetricsPersistenceService>();

// ----- سرویس‌های متن -----
builder.Services.AddSingleton<ITextAnalysisQueue, MemoryTextAnalysisQueue>();
builder.Services.AddSingleton<ITextAnalysisRepository, MemoryTextAnalysisRepository>();
// ----- Scheduling (راهکار ۱: صف‌بندی آگاه از تأخیر) -----
builder.Services.Configure<SchedulingOptions>(
    builder.Configuration.GetSection(SchedulingOptions.SectionName));
builder.Services.AddSingleton<SchedulingState>();
builder.Services.AddSingleton<IRequestScheduler, LatencyAwareScheduler>();
// ----- ChatCoordinator (با تزریق IMetricsCollector) -----
// ----- KV Cache Policy (راهکار ۲: مدیریت اولویت‌محور) -----
builder.Services.Configure<KvCachePolicyOptions>(
    builder.Configuration.GetSection(KvCachePolicyOptions.SectionName));
builder.Services.AddSingleton<KvCachePolicyState>();
builder.Services.AddSingleton<IKvCacheEvictionPolicy, PriorityKvCachePolicy>();
// ----- Chunked Prefill (راهکار ۴) -----
builder.Services.Configure<ChunkedPrefillOptions>(
    builder.Configuration.GetSection(ChunkedPrefillOptions.SectionName));
builder.Services.AddSingleton<ChunkedPrefillState>();
// ----- Load Testing -----
builder.Services.AddSingleton<LoadTestService>();
builder.Services.AddSingleton(sp =>
    new ChatCoordinator(
        sp.GetRequiredService<ILogger<ChatCoordinator>>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IMetricsCollector>(),
        sp.GetRequiredService<SchedulingState>(),
        sp.GetRequiredService<IRequestScheduler>(),
        sp.GetRequiredService<IOptions<SchedulingOptions>>(),
        sp.GetRequiredService<KvCachePolicyState>(),
        sp.GetRequiredService<IKvCacheEvictionPolicy>(),
        sp.GetRequiredService<IOptions<KvCachePolicyOptions>>(),
        sp.GetRequiredService<ChunkedPrefillState>(),                    // ⬅️
        sp.GetRequiredService<IOptions<ChunkedPrefillOptions>>()));      // ⬅️
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatCoordinator>());

// ----- TextAnalysisWorker -----
builder.Services.AddHostedService<TextAnalysisWorker>();

builder.Services.AddControllersWithViews();

builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = int.MaxValue;
});

var app = builder.Build();

// ============================================================
// گام ۶: Middleware Pipeline
// ============================================================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.UseMiddleware<SessionAuthMiddleware>();

// ============================================================
// گام ۷: Map Hubs و Routes
// ============================================================
app.MapHub<MetricsHub>("/metricsHub");
app.MapHub<ChatHub>("/chatHub");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// ============================================================
// گام ۸: Bridge - اتصال MetricsCollector به SignalR و ChatCoordinator
// ============================================================
var collector = app.Services.GetRequiredService<MetricsCollector>();
var hubContext = app.Services.GetRequiredService<IHubContext<MetricsHub>>();
collector.OnSample += sample =>
{
    _ = hubContext.Clients.All.SendAsync("MetricUpdate", sample);
};

var coordinator = app.Services.GetRequiredService<ChatCoordinator>();
collector.ActiveConversationsProvider = () => coordinator.GetActiveConversationCount();
collector.ActiveSequencesProvider = () => coordinator.GetActiveSequenceCount();
collector.MaxSequencesProvider = () => coordinator.GetMaxSequences();
collector.QueuedRequestsProvider = () => coordinator.GetQueuedCount();
collector.KvCacheUsageProvider = () => coordinator.GetKvCacheUsagePercent();
// ============================================================
// Bridge: ارزیابی آستانه‌ها روی هر نمونه‌ی متریک
// ============================================================
var thresholdService = app.Services.GetRequiredService<IThresholdService>();

collector.OnSample += sample =>
{
    var breaches = thresholdService.Evaluate(sample);
    foreach (var b in breaches)
    {
        _ = hubContext.Clients.All.SendAsync("ThresholdBreach", b);
    }
};
app.Run();