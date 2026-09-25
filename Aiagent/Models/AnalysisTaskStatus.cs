using System;

namespace Aiagent.Models
{
    // 1. وضعیت‌های ممکن برای یک تسک (AnalysisTaskStatus.cs)
    public enum AnalysisTaskStatus
    {
        Queued,
        Processing,
        Completed,
        Failed
    }

    public enum AnalysisMode
    {
        DetailedDescription,
        ObjectDetection,
        Ocr
    }

    public sealed class ImageAnalysisTask
    {
        public required Guid Id { get; init; }
        public required string StoredFilePath { get; init; }
        public required string ContentType { get; init; }
        public required AnalysisMode Mode { get; init; }
        public AnalysisTaskStatus Status { get; set; }
        public string? Result { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
