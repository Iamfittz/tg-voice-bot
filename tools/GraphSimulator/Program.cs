using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

// A local stand-in for the parts of Microsoft Graph that the bot uses, built from the public documentation.
// It lets the whole flow run without a Microsoft 365 tenant. It does not prove compatibility with the real service.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SimulatorState>();
var app = builder.Build();

const int PageSize = 2;
const int MaxSubscriptionMinutes = 4230;
var tenantId = app.Configuration["Simulator:TenantId"] ?? "00000000-0000-0000-0000-000000000001";

app.MapGet("/", () => "Graph simulator. POST /simulate/calls?quality=good|poor&participants=3");

// Sign-in

app.MapPost("/{tenant}/oauth2/v2.0/token", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    return form["grant_type"] == "client_credentials" && form["client_secret"].ToString().Length > 0
        ? Results.Json(new { access_token = "simulated-token", expires_in = 3600, token_type = "Bearer" })
        : Results.BadRequest(new { error = "invalid_client" });
});

var graph = app.MapGroup("/v1.0").AddEndpointFilter(async (context, next) =>
    context.HttpContext.Request.Headers.Authorization == "Bearer simulated-token"
        ? await next(context)
        : Results.Unauthorized());

// Subscriptions

graph.MapGet("/subscriptions", (SimulatorState state) => Results.Json(new { value = state.Subscriptions.Values }));

graph.MapPost("/subscriptions", async (Subscription subscription, SimulatorState state, IHttpClientFactory httpFactory) =>
{
    var minutes = (subscription.ExpirationDateTime - DateTimeOffset.UtcNow).TotalMinutes;
    if (minutes > MaxSubscriptionMinutes)
        return GraphError(HttpStatusCode.BadRequest, "ExtensionError", "The expiration is too far in the future");
    if (minutes > 60 && string.IsNullOrEmpty(subscription.LifecycleNotificationUrl))
        return GraphError(HttpStatusCode.BadRequest, "ValidationError", "lifecycleNotificationUrl is required");
    if (string.IsNullOrEmpty(subscription.ClientState))
        return GraphError(HttpStatusCode.BadRequest, "ValidationError", "clientState is required");
    if (state.Subscriptions.Values.Any(s => s.Resource == subscription.Resource && s.ChangeType == subscription.ChangeType))
        return GraphError(HttpStatusCode.Conflict, "Conflict", "Subscription already exists for the requested combination");

    foreach (var url in new[] { subscription.NotificationUrl, subscription.LifecycleNotificationUrl! })
    {
        if (!await ValidatesEndpointAsync(httpFactory, url))
            return GraphError(HttpStatusCode.BadRequest, "ValidationError", $"Endpoint validation failed for {url}");
    }

    var created = subscription with { Id = Guid.NewGuid().ToString() };
    state.Subscriptions[created.Id!] = created;
    app.Logger.LogInformation("Created subscription {Id} after validating both endpoints", created.Id);
    return Results.Json(created, statusCode: StatusCodes.Status201Created);
});

graph.MapMethods("/subscriptions/{id}", ["PATCH"], (string id, SubscriptionRenewal renewal, SimulatorState state) =>
{
    if (!state.Subscriptions.TryGetValue(id, out var subscription))
        return GraphError(HttpStatusCode.NotFound, "ResourceNotFound", "The subscription was not found");
    if ((renewal.ExpirationDateTime - DateTimeOffset.UtcNow).TotalMinutes > MaxSubscriptionMinutes)
        return GraphError(HttpStatusCode.BadRequest, "ExtensionError", "The expiration is too far in the future");

    var renewed = subscription with { ExpirationDateTime = renewal.ExpirationDateTime };
    state.Subscriptions[id] = renewed;
    app.Logger.LogInformation("Renewed subscription {Id}", id);
    return Results.Json(renewed);
});

// Call records

graph.MapGet("/communications/callRecords/{id}", (string id, HttpRequest request, SimulatorState state) =>
{
    if (!state.Calls.TryGetValue(id, out var call))
        return GraphError(HttpStatusCode.NotFound, "ResourceNotFound", "The call record was not found");

    // Every first read of a call version is throttled, so the client has to honor Retry-After.
    if (state.Throttled.TryAdd($"{id}:{call.Version}", true))
    {
        request.HttpContext.Response.Headers.RetryAfter = "1";
        return GraphError(HttpStatusCode.TooManyRequests, "TooManyRequests", "Please retry again later");
    }

    var body = new Dictionary<string, object?>
    {
        ["id"] = call.Id,
        ["version"] = call.Version,
        ["type"] = call.Type,
        ["modalities"] = new[] { "audio" },
        ["startDateTime"] = call.Start,
        ["endDateTime"] = call.End
    };

    var expand = request.Query["$expand"].ToString();
    if (expand.StartsWith("sessions", StringComparison.Ordinal))
    {
        body["sessions"] = call.Sessions.Take(PageSize);
        if (call.Sessions.Count > PageSize)
            body["sessions@odata.nextLink"] = PageLink(request, id, "sessions", PageSize);
    }
    else if (expand == "participants_v2")
    {
        body["participants_v2"] = call.Participants.Take(PageSize);
        if (call.Participants.Count > PageSize)
            body["participants_v2@odata.nextLink"] = PageLink(request, id, "participants_v2", PageSize);
    }

    return Results.Json(body);
});

graph.MapGet("/communications/callRecords/{id}/{collection}", (string id, string collection, int skip, HttpRequest request, SimulatorState state) =>
{
    if (!state.Calls.TryGetValue(id, out var call))
        return GraphError(HttpStatusCode.NotFound, "ResourceNotFound", "The call record was not found");

    IReadOnlyList<object>? items = collection switch
    {
        "sessions" => call.Sessions,
        "participants_v2" => call.Participants,
        _ => null
    };
    if (items is null)
        return GraphError(HttpStatusCode.NotFound, "ResourceNotFound", "Unknown collection");

    var body = new Dictionary<string, object?> { ["value"] = items.Skip(skip).Take(PageSize) };
    if (items.Count > skip + PageSize)
        body["@odata.nextLink"] = PageLink(request, id, collection, skip + PageSize);
    return Results.Json(body);
});

// Controls for a demonstration

var simulate = app.MapGroup("/simulate");

simulate.MapPost("/calls", async (SimulatorState state, IHttpClientFactory httpFactory, string quality = "good", int participants = 2) =>
{
    var call = SimulatedCall.Create(Guid.NewGuid().ToString(), version: 1, quality, participants);
    state.Calls[call.Id] = call;
    await NotifyAsync(state, httpFactory, call.Id, "created");
    return Results.Ok(new { call.Id, call.Version, quality });
});

simulate.MapPost("/calls/{id}/update", async (string id, SimulatorState state, IHttpClientFactory httpFactory, string quality = "poor") =>
{
    if (!state.Calls.TryGetValue(id, out var call))
        return Results.NotFound();

    var updated = SimulatedCall.Create(id, call.Version + 1, quality, call.Participants.Count);
    state.Calls[id] = updated;
    await NotifyAsync(state, httpFactory, id, "updated");
    return Results.Ok(new { updated.Id, updated.Version, quality });
});

simulate.MapPost("/calls/{id}/resend", async (string id, SimulatorState state, IHttpClientFactory httpFactory) =>
{
    if (!state.Calls.ContainsKey(id))
        return Results.NotFound();

    await NotifyAsync(state, httpFactory, id, "updated");
    return Results.Ok(new { id });
});

simulate.MapPost("/lifecycle", async (SimulatorState state, IHttpClientFactory httpFactory, string lifecycleEvent = "reauthorizationRequired") =>
{
    using var http = httpFactory.CreateClient();
    foreach (var subscription in state.Subscriptions.Values)
    {
        using var response = await http.PostAsJsonAsync(subscription.LifecycleNotificationUrl, new
        {
            value = new[]
            {
                new
                {
                    subscriptionId = subscription.Id,
                    subscriptionExpirationDateTime = subscription.ExpirationDateTime,
                    tenantId,
                    clientState = subscription.ClientState,
                    lifecycleEvent
                }
            }
        });
        app.Logger.LogInformation("Lifecycle event {Event} answered {Status}", lifecycleEvent, (int)response.StatusCode);
    }
    return Results.Ok(new { lifecycleEvent });
});

app.Run();

async Task<bool> ValidatesEndpointAsync(IHttpClientFactory httpFactory, string url)
{
    const string token = "Validation: Testing client application reachability for subscription Request-Id: 1a2b";

    using var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(10);
    var target = $"{url}{(url.Contains('?') ? '&' : '?')}validationToken={Uri.EscapeDataString(token)}";

    try
    {
        using var response = await http.PostAsync(target, new StringContent("", null, "text/plain"));
        return response.StatusCode == HttpStatusCode.OK
            && response.Content.Headers.ContentType?.MediaType == "text/plain"
            && await response.Content.ReadAsStringAsync() == token;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning("Could not reach {Url}: {Message}", url, ex.Message);
        return false;
    }
}

async Task NotifyAsync(SimulatorState state, IHttpClientFactory httpFactory, string callId, string changeType)
{
    using var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(10);

    foreach (var subscription in state.Subscriptions.Values)
    {
        var resource = $"communications/callRecords/{callId}";
        var watch = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync(subscription.NotificationUrl, new
        {
            value = new[]
            {
                new
                {
                    subscriptionId = subscription.Id,
                    clientState = subscription.ClientState,
                    changeType,
                    resource,
                    subscriptionExpirationDateTime = subscription.ExpirationDateTime,
                    resourceData = new Dictionary<string, string>
                    {
                        ["@odata.type"] = "#Microsoft.Graph.callRecord",
                        ["@odata.id"] = resource,
                        ["id"] = callId
                    },
                    tenantId
                }
            }
        });
        app.Logger.LogInformation(
            "Notification for {CallId} answered {Status} in {Ms} ms (Graph allows 3000 ms)",
            callId, (int)response.StatusCode, watch.ElapsedMilliseconds);
    }
}

static string PageLink(HttpRequest request, string id, string collection, int skip) =>
    $"{request.Scheme}://{request.Host}/v1.0/communications/callRecords/{id}/{collection}?skip={skip}";

static IResult GraphError(HttpStatusCode status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: (int)status);

internal sealed record Subscription(
    string? Id,
    string Resource,
    string ChangeType,
    string NotificationUrl,
    string? LifecycleNotificationUrl,
    DateTimeOffset ExpirationDateTime,
    string ClientState);

internal sealed record SubscriptionRenewal(DateTimeOffset ExpirationDateTime);

internal sealed class SimulatorState
{
    public ConcurrentDictionary<string, Subscription> Subscriptions { get; } = new();
    public ConcurrentDictionary<string, SimulatedCall> Calls { get; } = new();
    public ConcurrentDictionary<string, bool> Throttled { get; } = new();
}

internal sealed record SimulatedCall(
    string Id,
    long Version,
    string Type,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<object> Sessions,
    IReadOnlyList<object> Participants)
{
    private const int SessionCount = 3;
    private static readonly string[] Names = ["Partner One", "Partner Two", "Partner Three", "Partner Four", "Partner Five"];

    public static SimulatedCall Create(string id, long version, string quality, int participants)
    {
        var count = Math.Clamp(participants, 2, Names.Length);
        var end = DateTimeOffset.UtcNow;
        var start = end.AddSeconds(-192);
        var poor = quality == "poor";

        var sessions = Enumerable.Range(0, SessionCount)
            .Select(index => (object)new
            {
                id = Guid.NewGuid(),
                modalities = new[] { "audio" },
                startDateTime = start,
                endDateTime = end,
                segments = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        startDateTime = start,
                        endDateTime = end,
                        media = new[]
                        {
                            new
                            {
                                label = "main-audio",
                                streams = new[]
                                {
                                    Stream("callerToCallee", poor && index == 0),
                                    Stream("calleeToCaller", false)
                                }
                            }
                        }
                    }
                }
            })
            .ToList();

        var people = Names.Take(count)
            .Select(name => (object)new
            {
                id = Guid.NewGuid(),
                identity = new { user = new { id = Guid.NewGuid(), displayName = name } }
            })
            .ToList();

        return new SimulatedCall(id, version, count > 2 ? "groupCall" : "peerToPeer", start, end, sessions, people);
    }

    private static object Stream(string direction, bool poor) => new
    {
        streamId = Guid.NewGuid().ToString("N"),
        streamDirection = direction,
        packetUtilization = 9600,
        averageRoundTripTime = poor ? "PT0.64S" : "PT0.045S",
        averagePacketLossRate = poor ? 0.12 : 0.001,
        averageJitter = poor ? "PT0.045S" : "PT0.008S"
    };
}
