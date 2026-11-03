namespace VoiceBot.Domain;

public enum TranscriptionStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public sealed class Transcription
{
    public long Id { get; set; }
    public long TelegramUserId { get; set; }
    public long ChatId { get; set; }
    public int DurationSeconds { get; set; }
    public string Language { get; set; } = RecognitionLanguage.Auto;
    public TranscriptionStatus Status { get; set; } = TranscriptionStatus.Queued;
    public string? Text { get; set; }
    public string? Error { get; set; }
    public long? ProcessingMs { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}
