using Aiagent.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aiagent.Services;

public class ImageAnalysisWorker : BackgroundService
{
    private readonly IImageAnalysisQueue _queue;
    private readonly IImageAnalysisRepository _repository;
    private readonly ILogger<ImageAnalysisWorker> _logger;
    private readonly InferenceSession _session;
    private string[]? _labels;

    // مسیر فایل‌ها
    private const string ModelPath = "models/resnet50-v2-7.onnx";
    private const string LabelsPath = "models/synset.txt";

    public ImageAnalysisWorker(
        IImageAnalysisQueue queue,
        IImageAnalysisRepository repository,
        ILogger<ImageAnalysisWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _logger = logger;

        // بارگذاری مدل ONNX
        _session = new InferenceSession(ModelPath);

        // نمایش اطلاعات مدل در کنسول (برای دیباگ)
        PrintModelMetadata();

        _logger.LogInformation("مدل ONNX با موفقیت بارگذاری شد.");
    }

    private void PrintModelMetadata()
    {
        _logger.LogInformation("=== Inputs ===");
        foreach (var input in _session.InputMetadata)
        {
            _logger.LogInformation($"Name: {input.Key}, Shape: {string.Join(", ", input.Value.Dimensions)}");
        }

        _logger.LogInformation("=== Outputs ===");
        foreach (var output in _session.OutputMetadata)
        {
            _logger.LogInformation($"Name: {output.Key}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var task = await _queue.DequeueAsync(stoppingToken);
            if (task == null) continue;

            try
            {
                task.Status = AnalysisTaskStatus.Processing;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);

                _logger.LogInformation("شروع پردازش تسک {TaskId}", task.Id);

                var imageBytes = LoadImage(task.StoredFilePath);
                var inputTensor = PreprocessImage(imageBytes);
                var rawOutput = PerformInference(inputTensor);
                var analysisResult = PostprocessOutput(rawOutput);

                var resultJson = JsonSerializer.Serialize(new { Description = analysisResult });

                task.Status = AnalysisTaskStatus.Completed;
                task.Result = resultJson;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);

                _logger.LogInformation("تسک {TaskId} با موفقیت به پایان رسید.", task.Id);
            }
            catch (Exception ex)
            {
                task.Status = AnalysisTaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await _repository.UpdateAsync(task);
                _logger.LogError(ex, "تسک {TaskId} با خطا مواجه شد.", task.Id);
            }
        }
    }

    private byte[] LoadImage(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"فایل تصویر در مسیر {filePath} یافت نشد.");

        return File.ReadAllBytes(filePath);
    }

    private DenseTensor<float> PreprocessImage(byte[] imageBytes)
    {
        using var image = Image.Load<Rgb24>(imageBytes);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(224, 224),
            Mode = ResizeMode.Crop
        }));

        var input = new DenseTensor<float>(new[] { 1, 3, 224, 224 });

        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var stddev = new[] { 0.229f, 0.224f, 0.225f };

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> pixelSpan = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    input[0, 0, y, x] = ((pixelSpan[x].R / 255f) - mean[0]) / stddev[0];
                    input[0, 1, y, x] = ((pixelSpan[x].G / 255f) - mean[1]) / stddev[1];
                    input[0, 2, y, x] = ((pixelSpan[x].B / 255f) - mean[2]) / stddev[2];
                }
            }
        });

        return input;
    }

    private float[] PerformInference(DenseTensor<float> inputTensor)
    {
        // نام ورودی مدل ResNet50-v2 معمولاً "data" است
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("data", inputTensor)
        };

        using var results = _session.Run(inputs);

        // نام خروجی معمولاً "output" یا "resnetv22_dense0_fwd" است
        // اما چون فقط یک خروجی داریم، می‌توانیم اولین آن را بگیریم
        var output = results.First().AsTensor<float>();

        return output.ToArray();
    }

    private string PostprocessOutput(float[] output)
    {
        _labels ??= LoadLabels();

        int maxIndex = 0;
        float maxValue = output[0];
        for (int i = 1; i < output.Length; i++)
        {
            if (output[i] > maxValue)
            {
                maxValue = output[i];
                maxIndex = i;
            }
        }

        string label = _labels[maxIndex];
        float confidence = maxValue * 100;
        return $"{label} ({(int)confidence}%)";
    }

    private string[] LoadLabels()
    {
        if (!File.Exists(LabelsPath))
            throw new FileNotFoundException($"فایل لیبل‌ها در مسیر {LabelsPath} یافت نشد.");

        return File.ReadAllLines(LabelsPath);
    }
}