using Microsoft.EntityFrameworkCore;
using VoiceBot.Domain;

namespace VoiceBot.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Transcription> Transcriptions => Set<Transcription>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<CallRecordEntry> CallRecords => Set<CallRecordEntry>();
    public DbSet<PendingCallRecord> PendingCallRecords => Set<PendingCallRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var transcription = modelBuilder.Entity<Transcription>();
        transcription.Property(t => t.Status).HasConversion<string>();
        transcription.HasIndex(t => new { t.TelegramUserId, t.Status });
        transcription.HasIndex(t => t.CreatedAtUtc);

        // The key is the Telegram user id, not a generated value.
        modelBuilder.Entity<UserSettings>().Property(s => s.Id).ValueGeneratedNever();

        var callRecord = modelBuilder.Entity<CallRecordEntry>();
        callRecord.Property(c => c.Quality).HasConversion<string>();
        callRecord.HasIndex(c => c.UpdatedAtUtc);

        modelBuilder.Entity<PendingCallRecord>().HasIndex(p => new { p.GaveUp, p.NextAttemptUtc });
    }
}
