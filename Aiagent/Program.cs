using Aiagent.Hubs;
using Aiagent.Services;
using LLama.Native;
using Microsoft.AspNetCore.Http.Features;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// ✅ اصلاح اصلی: فعال‌سازی بک‌اند CUDA برای LLamaSharp
// بدون این خط، LLamaSharp بک‌اند CPU را بارگذاری می‌کند و
// در نتیجه پردازش ۱۰۰٪ روی CPU انجام می‌شود و GPU تقریباً بیکار می‌ماند.
// ============================================================
var useCuda = builder.Configuration.GetValue("Llm:Cuda", true);
var nativeLogging = builder.Configuration.GetValue("Llm:NativeLogging", true);

if (nativeLogging)
{
    NativeLibraryConfig.All.WithLogCallback((level, message) =>
        Console.WriteLine($"[llama.cpp] {message}"));
}

NativeLibraryConfig.All.WithCuda(useCuda);

Console.WriteLine($"🖥️ OS: {RuntimeInformation.OSDescription}");
Console.WriteLine($"🏗️ Architecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"🎮 CUDA backend: {useCuda}");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// سرویس‌های تصویر
builder.Services.AddSingleton<IImageAnalysisQueue, MemoryImageAnalysisQueue>();
builder.Services.AddSingleton<IImageAnalysisRepository, MemoryImageAnalysisRepository>();
builder.Services.AddHostedService<ImageAnalysisWorker>();

// سرویس‌های متن (صف و مخزن)
builder.Services.AddSingleton<ITextAnalysisQueue, MemoryTextAnalysisQueue>();
builder.Services.AddSingleton<ITextAnalysisRepository, MemoryTextAnalysisRepository>();

// ✅ ChatCoordinator با IConfiguration
builder.Services.AddSingleton(sp =>
    new ChatCoordinator(
        sp.GetRequiredService<ILogger<ChatCoordinator>>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatCoordinator>());

// ✅ TextAnalysisWorker جدید (از ChatCoordinator استفاده می‌کند)
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<ChatHub>("/chatHub");

app.Run();