using System.ComponentModel.DataAnnotations;

namespace VoiceBot;

public sealed class TelegramOptions
{
    [Required]
    public string Token { get; set; } = "";
}

public sealed class WhisperOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    [Required]
    public string Language { get; set; } = "auto";
}

public sealed class LimitOptions
{
    [Range(1, 7200)]
    public int MaxDurationSeconds { get; set; } = 300;

    // Telegram lets bots download files up to 20 MB.
    [Range(1, 20 * 1024 * 1024)]
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    [Range(1, 1000)]
    public int QueueCapacity { get; set; } = 20;

    [Range(1, 100)]
    public int MaxPendingPerUser { get; set; } = 3;

    [Range(1, 7200)]
    public int JobTimeoutSeconds { get; set; } = 600;
}
