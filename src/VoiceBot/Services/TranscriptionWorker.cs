namespace VoiceBot.Services;

// A single consumer on purpose: parallel jobs would only compete for the same CPU.
public sealed class TranscriptionWorker(
    TranscriptionQueue queue,
    IServiceScopeFactory scopes,
    ILogger<TranscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<TranscriptionProcessor>();
                await processor.ProcessAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad job must not stop the queue.
                logger.LogError(ex, "Unexpected failure in transcription {TranscriptionId}", job.TranscriptionId);
            }
        }
    }
}
