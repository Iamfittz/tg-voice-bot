using Microsoft.EntityFrameworkCore;
using VoiceBot.Domain;

namespace VoiceBot.Data;

public static class DatabaseInitializer
{
    public const string InterruptedError = "Interrupted by a service restart";

    // The queue lives in memory, so anything unfinished before a restart will never be processed.
    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await db.Transcriptions
            .Where(t => t.Status == TranscriptionStatus.Queued || t.Status == TranscriptionStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TranscriptionStatus.Failed)
                .SetProperty(t => t.Error, InterruptedError), ct);
    }
}
