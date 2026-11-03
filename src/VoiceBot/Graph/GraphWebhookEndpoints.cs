using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VoiceBot.Data;

namespace VoiceBot.Graph;

public static class GraphWebhookEndpoints
{
    public const string NotificationsPath = "/graph/notifications";
    public const string LifecyclePath = "/graph/lifecycle";

    private const string LoggerName = "VoiceBot.Graph.Webhooks";
    private const long MaxBodyBytes = 1024 * 1024;
    private const int MaxCallRecordIdLength = 128;

    public static void MapGraphWebhooks(this WebApplication app)
    {
        app.MapPost(NotificationsPath, HandleNotificationsAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .ExcludeFromDescription();

        app.MapPost(LifecyclePath, HandleLifecycleAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleNotificationsAsync(
        HttpRequest request,
        [FromQuery] string? validationToken,
        AppDbContext db,
        IOptions<GraphOptions> options,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (validationToken is not null)
            return Results.Text(validationToken, "text/plain");

        var batch = await ReadBatchAsync(request, ct);
        if (batch is null)
            return Results.BadRequest();

        foreach (var notification in batch.Value ?? [])
        {
            if (!IsAuthentic(notification, options.Value))
            {
                loggers.CreateLogger(LoggerName).LogWarning("Rejected a Graph notification with a wrong client state or tenant");
                continue;
            }

            if (notification.ResourceData?.Id is { Length: > 0 and <= MaxCallRecordIdLength } id)
                await AddPendingAsync(db, id, time.GetUtcNow().UtcDateTime, ct);
        }

        // Fetching the record is too slow for this request: Graph allows 3 seconds.
        // Any failure above becomes a 5xx, and Graph sends the notification again.
        return Results.Accepted();
    }

    private static async Task<IResult> HandleLifecycleAsync(
        HttpRequest request,
        [FromQuery] string? validationToken,
        GraphSubscriptionSignal signal,
        IOptions<GraphOptions> options,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (validationToken is not null)
            return Results.Text(validationToken, "text/plain");

        var batch = await ReadBatchAsync(request, ct);
        if (batch is null)
            return Results.BadRequest();

        var logger = loggers.CreateLogger(LoggerName);
        foreach (var notification in batch.Value ?? [])
        {
            if (!IsAuthentic(notification, options.Value))
            {
                logger.LogWarning("Rejected a Graph lifecycle notification with a wrong client state or tenant");
                continue;
            }

            logger.LogInformation("Graph lifecycle event {Event}", notification.LifecycleEvent);
            if (notification.LifecycleEvent is "reauthorizationRequired" or "subscriptionRemoved")
                signal.Request();
        }

        return Results.Accepted();
    }

    private static async Task<ChangeNotificationBatch?> ReadBatchAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<ChangeNotificationBatch>(
                request.Body, JsonSerializerOptions.Web, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAuthentic(ChangeNotification notification, GraphOptions options) =>
        SecretEquals(notification.ClientState, options.ClientState)
        && string.Equals(notification.TenantId, options.TenantId, StringComparison.OrdinalIgnoreCase);

    private static bool SecretEquals(string? actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual ?? ""),
            Encoding.UTF8.GetBytes(expected));

    private static Task AddPendingAsync(AppDbContext db, string id, DateTime now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO PendingCallRecords (Id, Revision, Attempts, GaveUp, ReceivedAtUtc, NextAttemptUtc)
            VALUES ({id}, 1, 0, 0, {now}, {now})
            ON CONFLICT (Id) DO UPDATE SET
                Revision = Revision + 1, Attempts = 0, GaveUp = 0, LastError = NULL, NextAttemptUtc = {now}
            """, ct);
}
