using System.ComponentModel.DataAnnotations;

namespace VoiceBot;

public sealed class TelegramOptions
{
    public bool Enabled { get; set; } = true;

    public string Token { get; set; } = "";
}

public sealed class GraphOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    // Graph calls the webhooks here, so the address must be public and use HTTPS.
    public string PublicBaseUrl { get; set; } = "";

    // Graph repeats this secret in every notification. Its limit is 128 characters.
    [MaxLength(128)]
    public string ClientState { get; set; } = "";

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0/";
    public string LoginBaseUrl { get; set; } = "https://login.microsoftonline.com/";
    public long? NotifyChatId { get; set; }

    // Graph allows at most 4230 minutes for call records.
    [Range(45, 4230)]
    public int SubscriptionLifetimeMinutes { get; set; } = 4200;

    [Range(1, 4000)]
    public int RenewalIntervalMinutes { get; set; } = 360;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(PublicBaseUrl)
        && !string.IsNullOrWhiteSpace(ClientState);
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
