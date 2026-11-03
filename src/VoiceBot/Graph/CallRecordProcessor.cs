using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Services;

namespace VoiceBot.Graph;

public sealed class CallRecordProcessor(
    AppDbContext db,
    IGraphClient graph,
    IBotGateway bot,
    IOptions<GraphOptions> options,
    TimeProvider time,
    ILogger<CallRecordProcessor> logger)
{
    private const string AudioLabelPrefix = "main-audio";

    // With doubling delays, 8 attempts cover about 4 hours.
    public const int MaxAttempts = 8;

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var pending = await db.PendingCallRecords
            .AsNoTracking()
            .Where(p => !p.GaveUp && p.NextAttemptUtc <= now)
            .OrderBy(p => p.NextAttemptUtc)
            .FirstOrDefaultAsync(ct);
        if (pending is null)
            return false;

        try
        {
            var record = await graph.GetCallRecordAsync(pending.Id, ct);
            await SaveAndNotifyAsync(record, ct);

            await db.PendingCallRecords
                .Where(p => p.Id == pending.Id && p.Revision == pending.Revision)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process call record {CallRecordId}", pending.Id);
            await RegisterFailureAsync(pending, ex.Message, now, ct);
        }

        return true;
    }

    private async Task SaveAndNotifyAsync(GraphCallRecord record, CancellationToken ct)
    {
        var entry = await db.CallRecords.FirstOrDefaultAsync(c => c.Id == record.Id, ct);

        // A retried notification or a repeated fetch must not create a second message.
        if (entry is not null && entry.Version >= record.Version)
            return;

        var isNew = entry is null;
        var previousQuality = entry?.Quality;
        if (entry is null)
        {
            entry = new CallRecordEntry { Id = record.Id };
            db.CallRecords.Add(entry);
        }

        Apply(record, entry);
        await db.SaveChangesAsync(ct);

        if (isNew || entry.Quality != previousQuality)
            await NotifyAsync(entry, ct);
    }

    private void Apply(GraphCallRecord record, CallRecordEntry entry)
    {
        var participants = (record.Participants ?? []).Select(p => p.Name).Distinct().ToList();
        var quality = CallQualityClassifier.Summarize(AudioStreams(record));

        entry.Version = record.Version;
        entry.Type = record.Type ?? "unknown";
        entry.Modalities = string.Join(',', record.Modalities ?? []);
        entry.Participants = string.Join(", ", participants);
        entry.ParticipantCount = participants.Count;
        entry.StartUtc = record.StartDateTime?.UtcDateTime;
        entry.EndUtc = record.EndDateTime?.UtcDateTime;
        entry.DurationSeconds = record.StartDateTime is { } start && record.EndDateTime is { } end
            ? (int)Math.Max((end - start).TotalSeconds, 0)
            : 0;
        entry.Quality = quality.Quality;
        entry.AudioStreams = quality.AudioStreams;
        entry.PoorStreams = quality.PoorStreams;
        entry.MaxRoundTripMs = quality.MaxRoundTripMs;
        entry.MaxPacketLossRate = quality.MaxPacketLossRate;
        entry.MaxJitterMs = quality.MaxJitterMs;
        entry.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
    }

    private static List<AudioStreamMetrics> AudioStreams(GraphCallRecord record) =>
        (record.Sessions ?? [])
            .SelectMany(session => session.Segments ?? [])
            .SelectMany(segment => segment.Media ?? [])
            .Where(media => media.Label?.StartsWith(AudioLabelPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(media => media.Streams ?? [])
            .Select(stream => new AudioStreamMetrics(
                stream.PacketUtilization,
                stream.AverageRoundTripTime,
                stream.AveragePacketLossRate,
                stream.AverageJitter))
            .ToList();

    private async Task NotifyAsync(CallRecordEntry entry, CancellationToken ct)
    {
        if (options.Value.NotifyChatId is not { } chatId)
            return;

        try
        {
            await bot.SendAsync(chatId, BotTexts.CallSummary(entry), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The record is already saved and available through the API.
            logger.LogError(ex, "Failed to send the summary of call record {CallRecordId}", entry.Id);
        }
    }

    private async Task RegisterFailureAsync(PendingCallRecord pending, string error, DateTime now, CancellationToken ct)
    {
        var attempts = pending.Attempts + 1;
        var gaveUp = attempts >= MaxAttempts;
        var nextAttempt = now.AddMinutes(Math.Pow(2, attempts - 1));

        if (gaveUp)
            logger.LogError("Giving up on call record {CallRecordId} after {Attempts} attempts", pending.Id, attempts);

        await db.PendingCallRecords
            .Where(p => p.Id == pending.Id && p.Revision == pending.Revision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Attempts, attempts)
                .SetProperty(p => p.GaveUp, gaveUp)
                .SetProperty(p => p.LastError, error)
                .SetProperty(p => p.NextAttemptUtc, nextAttempt), ct);
    }
}
