namespace Aiagent.Models
{
    public enum TextAnalysisStatus
    {
        Queued,
        Processing,
        Completed,
        Failed
    }
    public sealed class TextAnalysisTask
    {
        public required Guid Id { get; init; }
        public required string UserPrompt { get; init; }   // سوال کاربر
        public string? SystemPrompt { get; init; }          // دستور سیستم (اختیاری)
        public TextAnalysisStatus Status { get; set; }
        public string? Result { get; set; }                 // پاسخ تولیدشده
        public string? ErrorMessage { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

}
