using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using VoiceBot.Domain;
using VoiceBot.Services;

namespace VoiceBot.Telegram;

public sealed class TelegramGateway(ITelegramBotClient client) : IBotGateway
{
    public async Task<int> SendAsync(long chatId, string text, CancellationToken ct)
    {
        var message = await client.SendMessage(chatId, text, cancellationToken: ct);
        return message.MessageId;
    }

    public Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct)
    {
        var buttons = RecognitionLanguage.Options
            .Select(o => new[] { InlineKeyboardButton.WithCallbackData(o.ButtonLabel, UpdateRouter.LanguageCallbackPrefix + o.Code) });
        return client.SendMessage(chatId, text, replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
    }

    public Task AnswerCallbackAsync(string callbackId, CancellationToken ct) =>
        client.AnswerCallbackQuery(callbackId, cancellationToken: ct);

    public Task EditAsync(long chatId, int messageId, string text, CancellationToken ct) =>
        client.EditMessageText(chatId, messageId, text, cancellationToken: ct);

    public async Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct)
    {
        await using var destination = System.IO.File.Create(destinationPath);
        await client.GetInfoAndDownloadFile(fileId, destination, ct);
    }
}
