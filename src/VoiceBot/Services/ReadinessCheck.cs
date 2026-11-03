using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VoiceBot.Data;

namespace VoiceBot.Services;

public sealed class ReadinessCheck(
    AppDbContext db,
    WhisperClient whisper,
    IOptions<TelegramOptions> telegram) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct))
            return HealthCheckResult.Unhealthy("Database is unavailable");
        // Without Telegram no voice messages arrive, so nothing needs Whisper.
        if (telegram.Value.Enabled && !await whisper.IsReachableAsync(ct))
            return HealthCheckResult.Unhealthy("Whisper is unreachable");
        return HealthCheckResult.Healthy();
    }
}
