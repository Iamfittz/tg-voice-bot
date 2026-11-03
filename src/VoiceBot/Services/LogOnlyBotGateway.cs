namespace VoiceBot.Services;

// Used when Telegram is turned off, so the Graph part runs without a bot token.
public sealed class LogOnlyBotGateway(ILogger<LogOnlyBotGateway> logger) : IBotGateway
{
    public Task<int> SendAsync(long chatId, string text, CancellationToken ct)
    {
        logger.LogInformation("Message to chat {ChatId}:\n{Text}", chatId, text);
        return Task.FromResult(0);
    }

    public Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct) => Task.CompletedTask;

    public Task AnswerCallbackAsync(string callbackId, CancellationToken ct) => Task.CompletedTask;

    public Task EditAsync(long chatId, int messageId, string text, CancellationToken ct) => Task.CompletedTask;

    public Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct) =>
        throw new NotSupportedException("Telegram is turned off");
}
