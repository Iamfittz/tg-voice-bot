using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VoiceBot.Graph;

public interface IGraphClient
{
    Task<IReadOnlyList<GraphSubscription>> ListSubscriptionsAsync(CancellationToken ct);
    Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscription subscription, CancellationToken ct);
    Task RenewSubscriptionAsync(string id, DateTimeOffset expiration, CancellationToken ct);
    Task<GraphCallRecord> GetCallRecordAsync(string id, CancellationToken ct);
}

public sealed class GraphClient(HttpClient http, GraphTokenProvider tokens, ILogger<GraphClient> logger) : IGraphClient
{
    private const int MaxAttempts = 5;

    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = Task.Delay;

    public async Task<IReadOnlyList<GraphSubscription>> ListSubscriptionsAsync(CancellationToken ct)
    {
        var subscriptions = new List<GraphSubscription>();
        await ReadPagesAsync("subscriptions", subscriptions, ct);
        return subscriptions;
    }

    public async Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscription subscription, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "subscriptions", subscription, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<GraphSubscription>(response, ct);
    }

    public async Task RenewSubscriptionAsync(string id, DateTimeOffset expiration, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Patch,
            $"subscriptions/{Uri.EscapeDataString(id)}",
            new { expirationDateTime = expiration },
            ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<GraphCallRecord> GetCallRecordAsync(string id, CancellationToken ct)
    {
        var path = $"communications/callRecords/{Uri.EscapeDataString(id)}";

        // Graph expands sessions or participants in one request, not both.
        var record = await GetAsync<GraphCallRecord>($"{path}?$expand=sessions($expand=segments)", ct);
        var sessions = record.Sessions ?? [];
        await ReadPagesAsync(record.SessionsNextLink, sessions, ct);

        var withParticipants = await GetAsync<GraphCallRecord>($"{path}?$expand=participants_v2", ct);
        var participants = withParticipants.Participants ?? [];
        await ReadPagesAsync(withParticipants.ParticipantsNextLink, participants, ct);

        return record with { Sessions = sessions, Participants = participants };
    }

    private async Task ReadPagesAsync<T>(string? url, List<T> items, CancellationToken ct)
    {
        while (url is not null)
        {
            var page = await GetAsync<GraphPage<T>>(url, ct);
            items.AddRange(page.Value ?? []);
            url = page.NextLink;
        }
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAsync<object>(HttpMethod.Get, url, null, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync<TBody>(HttpMethod method, string url, TBody? body, CancellationToken ct)
    {
        var target = Resolve(url);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
            if (body is not null)
                request.Content = JsonContent.Create(body);

            var response = await http.SendAsync(request, ct);
            if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                return response;

            var delay = RetryDelay(response, attempt);
            logger.LogWarning(
                "Graph answered {Status}, attempt {Attempt} of {MaxAttempts}, waiting {Delay}",
                (int)response.StatusCode, attempt, MaxAttempts, delay);
            response.Dispose();
            await Wait(delay, ct);
        }
    }

    // A next page link comes from the response, and the request to it carries the access token.
    private Uri Resolve(string url)
    {
        var target = new Uri(http.BaseAddress!, url);
        if (target.Host != http.BaseAddress!.Host || target.Scheme != http.BaseAddress.Scheme)
            throw new InvalidOperationException($"Refusing to send the Graph token to {target.Host}");
        return target;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt) =>
        response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var error = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Graph answered {(int)response.StatusCode}: {error}", null, response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(ct)
        ?? throw new InvalidOperationException("Graph returned an empty response");
}
