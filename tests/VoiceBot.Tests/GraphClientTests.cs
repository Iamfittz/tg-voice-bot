using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceBot;
using VoiceBot.Graph;
using Xunit;

namespace VoiceBot.Tests;

public sealed class GraphClientTests
{
    private const string BaseUrl = "https://graph.test/v1.0/";

    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<TimeSpan> _waits = [];
    private int _tokenRequests;

    [Fact]
    public async Task Throttled_WaitsForRetryAfter_ThenRetries()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            if (++calls > 1)
                return Json("""{"value":[]}""");
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return throttled;
        });

        await client.ListSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal([TimeSpan.FromSeconds(10)], _waits);
    }

    [Fact]
    public async Task ThrottledWithoutHeader_BacksOffExponentially()
    {
        var calls = 0;
        var client = CreateClient(_ =>
            ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("""{"value":[]}"""));

        await client.ListSubscriptionsAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _waits);
    }

    [Fact]
    public async Task AlwaysThrottled_GivesUpAfterFiveAttempts()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListSubscriptionsAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task NotFound_IsNotRetried_AndKeepsStatusCode()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("gone") };
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCallRecordAsync("id", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("gone", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallRecord_ReadsEveryPageOfSessionsAndParticipants_AndParsesMetrics()
    {
        var client = CreateClient(request => request.RequestUri!.PathAndQuery switch
        {
            "/v1.0/communications/callRecords/call-1?$expand=sessions($expand=segments)" => Json(
                """
                {
                  "id": "call-1", "version": 3, "type": "peerToPeer", "modalities": ["audio"],
                  "startDateTime": "2026-11-03T10:00:00Z", "endDateTime": "2026-11-03T10:03:12Z",
                  "sessions": [{ "segments": [{ "media": [{ "label": "main-audio", "streams": [{
                      "packetUtilization": 9600, "averagePacketLossRate": 0.12,
                      "averageRoundTripTime": "PT0.64S", "averageJitter": "PT0.045S" }] }] }] }],
                  "sessions@odata.nextLink": "https://graph.test/v1.0/sessions-page-2"
                }
                """),
            "/v1.0/sessions-page-2" => Json("""{ "value": [{ "segments": [] }] }"""),
            "/v1.0/communications/callRecords/call-1?$expand=participants_v2" => Json(
                """
                {
                  "id": "call-1", "version": 3,
                  "participants_v2": [{ "id": "a", "identity": { "user": { "displayName": "Partner One" } } }],
                  "participants_v2@odata.nextLink": "https://graph.test/v1.0/participants-page-2"
                }
                """),
            "/v1.0/participants-page-2" => Json(
                """{ "value": [{ "id": "b", "identity": { "phone": { "id": "+380000000000" } } }] }"""),
            var other => throw new InvalidOperationException($"Unexpected request {other}")
        });

        var record = await client.GetCallRecordAsync("call-1", CancellationToken.None);

        Assert.Equal(3, record.Version);
        Assert.Equal(2, record.Sessions!.Count);
        Assert.Equal(["Partner One", "+380000000000"], record.Participants!.Select(p => p.Name));

        var stream = record.Sessions[0].Segments![0].Media![0].Streams![0];
        Assert.Equal(TimeSpan.FromMilliseconds(640), stream.AverageRoundTripTime);
        Assert.Equal(TimeSpan.FromMilliseconds(45), stream.AverageJitter);
    }

    [Fact]
    public async Task NextLinkToAnotherHost_DoesNotReceiveTheToken()
    {
        var client = CreateClient(_ => Json(
            """{ "id": "call-1", "version": 1, "sessions": [], "sessions@odata.nextLink": "https://evil.test/steal" }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCallRecordAsync("call-1", CancellationToken.None));

        Assert.DoesNotContain(_requests, r => r.RequestUri!.Host == "evil.test");
    }

    [Fact]
    public async Task CallRecordId_IsEscapedInTheUrl()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCallRecordAsync("a/../b?x", CancellationToken.None));

        Assert.StartsWith("/v1.0/communications/callRecords/a%2F..%2Fb%3Fx", _requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Token_IsSentAsBearer_AndRequestedOnce()
    {
        var client = CreateClient(_ => Json("""{"value":[]}"""));

        await client.ListSubscriptionsAsync(CancellationToken.None);
        await client.ListSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(1, _tokenRequests);
        Assert.All(_requests, r => Assert.Equal("Bearer test-token", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Token_IsRefreshedBeforeItExpires()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var client = CreateClient(_ => Json("""{"value":[]}"""), time);

        await client.ListSubscriptionsAsync(CancellationToken.None);
        time.Now = time.Now.AddMinutes(54);
        await client.ListSubscriptionsAsync(CancellationToken.None);
        Assert.Equal(1, _tokenRequests);

        time.Now = time.Now.AddMinutes(2);
        await client.ListSubscriptionsAsync(CancellationToken.None);
        Assert.Equal(2, _tokenRequests);
    }

    [Fact]
    public async Task NewSubscription_DoesNotSendAnId()
    {
        string? body = null;
        var client = CreateClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("""{"id":"new"}""");
        });

        await client.CreateSubscriptionAsync(
            new GraphSubscription(null, "communications/callRecords", "updated", "https://x/n", "https://x/l", DateTimeOffset.UtcNow, "secret"),
            CancellationToken.None);

        Assert.DoesNotContain("\"id\"", body);
        Assert.Contains("\"lifecycleNotificationUrl\"", body);
        Assert.Contains("\"clientState\":\"secret\"", body);
    }

    private GraphClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider? time = null)
    {
        var options = Options.Create(new GraphOptions
        {
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            LoginBaseUrl = "https://login.test/"
        });
        var tokens = new GraphTokenProvider(
            new SingleClientFactory(new StubHandler(_ =>
            {
                _tokenRequests++;
                return Json("""{"access_token":"test-token","expires_in":3600}""");
            })),
            options,
            time ?? TimeProvider.System);

        return new GraphClient(
            new HttpClient(new StubHandler(request =>
            {
                _requests.Add(request);
                return respond(request);
            })) { BaseAddress = new Uri(BaseUrl) },
            tokens,
            NullLogger<GraphClient>.Instance)
        {
            Wait = (delay, _) =>
            {
                _waits.Add(delay);
                return Task.CompletedTask;
            }
        };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
