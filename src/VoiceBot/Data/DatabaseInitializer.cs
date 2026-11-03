using Microsoft.EntityFrameworkCore;
using VoiceBot.Domain;

namespace VoiceBot.Data;

public static class DatabaseInitializer
{
    public const string InterruptedError = "Interrupted by a service restart";

    // The queue lives in memory, so anything unfinished before a restart will never be processed.
    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        await CreateMissingTablesAsync(db, ct);
        await db.Transcriptions
            .Where(t => t.Status == TranscriptionStatus.Queued || t.Status == TranscriptionStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TranscriptionStatus.Failed)
                .SetProperty(t => t.Error, InterruptedError), ct);
    }

    // EnsureCreated does nothing when the database file exists, so a deployed database
    // would never get a new table. This does not change columns of existing tables.
    private static Task CreateMissingTablesAsync(AppDbContext db, CancellationToken ct)
    {
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ");
        return db.Database.ExecuteSqlRawAsync(script, ct);
    }
}
