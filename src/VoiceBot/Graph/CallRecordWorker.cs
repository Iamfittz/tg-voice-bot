namespace VoiceBot.Graph;

public sealed class CallRecordWorker(
    IServiceScopeFactory scopes,
    ILogger<CallRecordWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<CallRecordProcessor>();
                processed = await processor.ProcessNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected failure while processing call records");
            }

            if (!processed)
                await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
