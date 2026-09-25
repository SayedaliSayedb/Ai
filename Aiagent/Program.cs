using Aiagent.Hubs;
using Aiagent.Services;
using LLama.Native;
using Microsoft.AspNetCore.Http.Features;
using System.Runtime.InteropServices;
//NativeLibraryConfig.All.WithLogCallback((level, message) =>
//{
//    // اینجا پیام لاگ را به هر جایی که می‌خواهید بفرستید
//    Console.WriteLine($"[LLamaSharp] {level}: {message}");
//});
//NativeLibraryConfig.All.WithCuda(true);
//Console.WriteLine($"🖥️ OS: {RuntimeInformation.OSDescription}");
//Console.WriteLine($"🏗️ Architecture: {RuntimeInformation.ProcessArchitecture}");

var builder = WebApplication.CreateBuilder(args);

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

// ❌ حذف کامل TextGenerator:
// builder.Services.AddSingleton<TextGenerator>();  ← این خط حذف شود

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