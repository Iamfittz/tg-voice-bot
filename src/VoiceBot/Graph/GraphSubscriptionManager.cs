using System.Net;
using Microsoft.Extensions.Options;

namespace VoiceBot.Graph;

public sealed class GraphSubscriptionManager(
    IGraphClient graph,
    IOptions<GraphOptions> options,
    TimeProvider time,
    ILogger<GraphSubscriptionManager> logger)
{
    public const string Resource = "communications/callRecords";

    private readonly GraphOptions _options = options.Value;

    // Graph is the source of truth, so a restart or a lost database does not create a duplicate (409).
    public async Task EnsureAsync(CancellationToken ct)
    {
        var expiration = time.GetUtcNow().AddMinutes(_options.SubscriptionLifetimeMinutes);
        var notificationUrl = Url(GraphWebhookEndpoints.NotificationsPath);

        var existing = (await graph.ListSubscriptionsAsync(ct)).FirstOrDefault(s =>
            string.Equals(s.Resource?.TrimStart('/'), Resource, StringComparison.OrdinalIgnoreCase)
            && s.NotificationUrl == notificationUrl);

        if (existing?.Id is { } id)
        {
            try
            {
                // Besides the expiration, this also reauthorizes the subscription.
                await graph.RenewSubscriptionAsync(id, expiration, ct);
                logger.LogInformation("Renewed Graph subscription {SubscriptionId} until {Expiration}", id, expiration);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Graph subscription {SubscriptionId} is gone, creating a new one", id);
            }
        }

        var created = await graph.CreateSubscriptionAsync(new GraphSubscription(
            Id: null,
            Resource: Resource,
            // Includes the first notification and the later versions of a record.
            ChangeType: "updated",
            NotificationUrl: notificationUrl,
            // Required for Teams resources when the subscription lives longer than an hour.
            LifecycleNotificationUrl: Url(GraphWebhookEndpoints.LifecyclePath),
            ExpirationDateTime: expiration,
            ClientState: _options.ClientState), ct);

        logger.LogInformation("Created Graph subscription {SubscriptionId} until {Expiration}", created.Id, expiration);
    }

    private string Url(string path) => _options.PublicBaseUrl.TrimEnd('/') + path;
}

public sealed class GraphSubscriptionSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A request is already waiting.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

public sealed class GraphSubscriptionService(
    IServiceScopeFactory scopes,
    GraphSubscriptionSignal signal,
    IHostApplicationLifetime lifetime,
    IOptions<GraphOptions> options,
    ILogger<GraphSubscriptionService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Creating a subscription makes Graph call the webhook, so the server must already be listening.
        await WaitForServerAsync(stoppingToken);

        var renewalInterval = TimeSpan.FromMinutes(options.Value.RenewalIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = renewalInterval;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GraphSubscriptionManager>().EnsureAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to create or renew the Graph subscription");
                delay = RetryDelay;
            }

            await signal.WaitAsync(delay, stoppingToken);
        }
    }

    private async Task WaitForServerAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(ct);
    }
}
