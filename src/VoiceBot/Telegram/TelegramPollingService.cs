using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace VoiceBot.Telegram;

// Long polling needs only outbound connections, so the server opens no public port for the bot.
public sealed class TelegramPollingService(
    ITelegramBotClient client,
    IServiceScopeFactory scopes,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterCommandsAsync(stoppingToken);

        var options = new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] };
        logger.LogInformation("Telegram polling started");
        await client.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, options, stoppingToken);
    }

    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        try
        {
            await client.SetMyCommands(
            [
                new BotCommand { Command = "lang", Description = "Змінити мову запису" },
                new BotCommand { Command = "forget", Description = "Видалити мої дані" }
            ], cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The menu is a convenience: the bot works without it.
            logger.LogWarning(ex, "Could not register bot commands");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<UpdateRouter>();
            if (update.Message is { } message)
                await router.HandleAsync(message, ct);
            else if (update.CallbackQuery is { } query)
                await router.HandleCallbackAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle update {UpdateId}", update.Id);
        }
    }

    private async Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram polling error");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}
