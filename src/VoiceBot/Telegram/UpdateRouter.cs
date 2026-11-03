using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Services;

namespace VoiceBot.Telegram;

public sealed class UpdateRouter(
    AppDbContext db,
    IBotGateway bot,
    TranscriptionQueue queue,
    IOptions<LimitOptions> limits,
    IOptions<WhisperOptions> whisper)
{
    public const string LanguageCallbackPrefix = "lang:";

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        // Answered first, otherwise the button keeps showing a spinner.
        await bot.AnswerCallbackAsync(query.Id, ct);

        if (query.Data?.StartsWith(LanguageCallbackPrefix, StringComparison.Ordinal) != true)
            return;
        var language = query.Data[LanguageCallbackPrefix.Length..];
        if (!RecognitionLanguage.IsSupported(language))
            return;

        await SaveLanguageAsync(query.From.Id, language, ct);

        var text = BotTexts.LanguageChanged(RecognitionLanguage.Name(language));
        if (query.Message is { } picker)
            await bot.EditAsync(picker.Chat.Id, picker.MessageId, text, ct);
        else
            await bot.SendAsync(query.From.Id, text, ct);
    }

    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        if (message.From is null)
            return;

        var media = Media.From(message);
        if (media is not null)
        {
            await AcceptMediaAsync(message.Chat.Id, message.From, media, ct);
            return;
        }

        var command = ParseCommand(message.Text);
        if (command == "forget")
        {
            await db.Transcriptions.Where(t => t.TelegramUserId == message.From.Id).ExecuteDeleteAsync(ct);
            await db.UserSettings.Where(s => s.Id == message.From.Id).ExecuteDeleteAsync(ct);
            await bot.SendAsync(message.Chat.Id, BotTexts.Forgotten, ct);
            return;
        }

        if (command is not null && RecognitionLanguage.IsSupported(command))
        {
            await SaveLanguageAsync(message.From.Id, command, ct);
            await bot.SendAsync(message.Chat.Id, BotTexts.LanguageChanged(RecognitionLanguage.Name(command)), ct);
            return;
        }

        if (command == "lang")
        {
            var settings = await db.UserSettings.FindAsync([message.From.Id], ct);
            var current = RecognitionLanguage.Resolve(settings?.Language, whisper.Value.Language);
            await bot.SendLanguagePickerAsync(
                message.Chat.Id, BotTexts.CurrentLanguage(RecognitionLanguage.Name(current)), ct);
            return;
        }

        await bot.SendAsync(message.Chat.Id, BotTexts.Welcome, ct);
        await bot.SendLanguagePickerAsync(message.Chat.Id, BotTexts.ChooseLanguage, ct);
    }

    private async Task AcceptMediaAsync(long chatId, User user, Media media, CancellationToken ct)
    {
        var pending = await db.Transcriptions.CountAsync(
            t => t.TelegramUserId == user.Id
                && (t.Status == TranscriptionStatus.Queued || t.Status == TranscriptionStatus.Processing),
            ct);

        var admission = VoiceAdmission.Check(media.DurationSeconds, media.FileBytes, pending, queue.HasSpace, limits.Value);
        if (admission != AdmissionResult.Accepted)
        {
            await bot.SendAsync(chatId, RejectionText(admission), ct);
            return;
        }

        var settings = await db.UserSettings.FindAsync([user.Id], ct);
        var language = RecognitionLanguage.Resolve(settings?.Language, whisper.Value.Language);

        var record = new Transcription
        {
            TelegramUserId = user.Id,
            ChatId = chatId,
            DurationSeconds = media.DurationSeconds,
            Language = language
        };
        db.Transcriptions.Add(record);
        await db.SaveChangesAsync(ct);

        var progressMessageId = await bot.SendAsync(chatId, BotTexts.Progress, ct);
        if (!queue.TryEnqueue(new TranscriptionJob(record.Id, chatId, media.FileId, language, progressMessageId)))
        {
            record.Status = TranscriptionStatus.Failed;
            record.Error = "Queue is full";
            await db.SaveChangesAsync(ct);
            await bot.EditAsync(chatId, progressMessageId, BotTexts.QueueFull, ct);
        }
    }

    private async Task SaveLanguageAsync(long userId, string language, CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync([userId], ct);
        if (settings is null)
            db.UserSettings.Add(new UserSettings { Id = userId, Language = language });
        else
            settings.Language = language;
        await db.SaveChangesAsync(ct);
    }

    // "/UK@my_bot extra" -> "uk"; anything that is not a command -> null.
    private static string? ParseCommand(string? text)
    {
        if (text is null || !text.StartsWith('/'))
            return null;
        return text[1..].Split(' ', '@', '\n')[0].ToLowerInvariant();
    }

    private string RejectionText(AdmissionResult admission) => admission switch
    {
        AdmissionResult.TooLong => BotTexts.TooLong(limits.Value.MaxDurationSeconds),
        AdmissionResult.TooBig => BotTexts.TooBig,
        AdmissionResult.UserBusy => BotTexts.UserBusy,
        _ => BotTexts.QueueFull
    };

    private sealed record Media(string FileId, int DurationSeconds, long? FileBytes)
    {
        public static Media? From(Message message) =>
            message.Voice is { } voice ? new Media(voice.FileId, voice.Duration, voice.FileSize)
            : message.Audio is { } audio ? new Media(audio.FileId, audio.Duration, audio.FileSize)
            : message.VideoNote is { } note ? new Media(note.FileId, note.Duration, note.FileSize)
            : null;
    }
}
