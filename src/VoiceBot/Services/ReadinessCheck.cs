using Microsoft.Extensions.Diagnostics.HealthChecks;
using VoiceBot.Data;

namespace VoiceBot.Services;

public sealed class ReadinessCheck(AppDbContext db, WhisperClient whisper) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct))
            return HealthCheckResult.Unhealthy("Database is unavailable");
        if (!await whisper.IsReachableAsync(ct))
            return HealthCheckResult.Unhealthy("Whisper is unreachable");
        return HealthCheckResult.Healthy();
    }
}
