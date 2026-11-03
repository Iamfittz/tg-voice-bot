using System.Diagnostics;
using Microsoft.Extensions.Options;
using VoiceBot.Data;
using VoiceBot.Domain;

namespace VoiceBot.Services;

public sealed class TranscriptionProcessor(
    AppDbContext db,
    IBotGateway bot,
    IAudioConverter converter,
    ITranscriber transcriber,
    IOptions<LimitOptions> limits,
    ILogger<TranscriptionProcessor> logger)
{
    public async Task ProcessAsync(TranscriptionJob job, CancellationToken stoppingToken)
    {
        // Null means the user ran /forget while the job was waiting in the queue.
        var record = await db.Transcriptions.FindAsync([job.TranscriptionId], stoppingToken);
        if (record is null)
            return;

        record.Status = TranscriptionStatus.Processing;
        await db.SaveChangesAsync(stoppingToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.Value.JobTimeoutSeconds));

        var workDir = Path.Combine(Path.GetTempPath(), "voicebot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var inputPath = Path.Combine(workDir, "input");
            var wavPath = Path.Combine(workDir, "audio.wav");

            await bot.DownloadAsync(job.FileId, inputPath, timeout.Token);
            await converter.ConvertToWavAsync(inputPath, wavPath, timeout.Token);
            record.Text = await transcriber.TranscribeAsync(wavPath, job.Language, timeout.Token);
            record.Status = TranscriptionStatus.Completed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription {TranscriptionId} failed", job.TranscriptionId);
            record.Status = TranscriptionStatus.Failed;
            record.Error = ex is OperationCanceledException ? "Timed out" : ex.Message;
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }

        record.ProcessingMs = stopwatch.ElapsedMilliseconds;
        record.CompletedAtUtc = DateTime.UtcNow;
        // Saved before replying, so a Telegram failure cannot lose a finished transcription.
        await db.SaveChangesAsync(stoppingToken);

        logger.LogInformation(
            "Transcription {TranscriptionId}: {Status}, audio {DurationSeconds} s, processed in {ProcessingMs} ms",
            record.Id, record.Status, record.DurationSeconds, record.ProcessingMs);

        await ReplyAsync(job, record, stoppingToken);
    }

    private async Task ReplyAsync(TranscriptionJob job, Transcription record, CancellationToken ct)
    {
        var text = record.Status == TranscriptionStatus.Failed
            ? BotTexts.Failed
            : string.IsNullOrWhiteSpace(record.Text) ? BotTexts.NoSpeech : record.Text;

        var parts = MessageSplitter.Split(text);
        try
        {
            await bot.EditAsync(job.ChatId, job.ProgressMessageId, parts[0], ct);
            foreach (var part in parts.Skip(1))
                await bot.SendAsync(job.ChatId, part, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not deliver transcription {TranscriptionId}", job.TranscriptionId);
        }
    }
}
