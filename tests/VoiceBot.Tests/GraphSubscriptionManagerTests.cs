using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceBot;
using VoiceBot.Graph;
using Xunit;

namespace VoiceBot.Tests;

public sealed class GraphSubscriptionManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private const string NotificationUrl = "https://bot.test/graph/notifications";

    private readonly FakeGraph _graph = new();
    private readonly GraphSubscriptionManager _manager;

    public GraphSubscriptionManagerTests()
    {
        _manager = new GraphSubscriptionManager(
            _graph,
            Options.Create(new GraphOptions
            {
                PublicBaseUrl = "https://bot.test/",
                ClientState = "secret",
                SubscriptionLifetimeMinutes = 4200
            }),
            new FixedTime(Now),
            NullLogger<GraphSubscriptionManager>.Instance);
    }

    [Fact]
    public async Task NoSubscription_CreatesOne_WithLifecycleUrlAndSecret()
    {
        await _manager.EnsureAsync(CancellationToken.None);

        var created = Assert.Single(_graph.Created);
        Assert.Equal("communications/callRecords", created.Resource);
        Assert.Equal("updated", created.ChangeType);
        Assert.Equal(NotificationUrl, created.NotificationUrl);
        Assert.Equal("https://bot.test/graph/lifecycle", created.LifecycleNotificationUrl);
        Assert.Equal("secret", created.ClientState);
        Assert.Equal(Now.AddMinutes(4200), created.ExpirationDateTime);
        Assert.Empty(_graph.Renewed);
    }

    [Theory]
    [InlineData("communications/callRecords")]
    [InlineData("/communications/callRecords")]
    [InlineData("/Communications/CallRecords")]
    public async Task ExistingSubscription_IsRenewed_NotDuplicated(string resource)
    {
        _graph.Existing.Add(Subscription("sub-1", resource, NotificationUrl));

        await _manager.EnsureAsync(CancellationToken.None);

        Assert.Empty(_graph.Created);
        Assert.Equal([("sub-1", Now.AddMinutes(4200))], _graph.Renewed);
    }

    [Theory]
    [InlineData("users/1/messages", NotificationUrl)]
    [InlineData("communications/callRecords", "https://other.test/graph/notifications")]
    public async Task OtherSubscriptions_AreIgnored(string resource, string notificationUrl)
    {
        _graph.Existing.Add(Subscription("other", resource, notificationUrl));

        await _manager.EnsureAsync(CancellationToken.None);

        Assert.Single(_graph.Created);
        Assert.Empty(_graph.Renewed);
    }

    [Fact]
    public async Task SubscriptionRemovedMeanwhile_IsCreatedAgain()
    {
        _graph.Existing.Add(Subscription("sub-1", "communications/callRecords", NotificationUrl));
        _graph.RenewError = new HttpRequestException("gone", null, HttpStatusCode.NotFound);

        await _manager.EnsureAsync(CancellationToken.None);

        Assert.Single(_graph.Created);
    }

    [Fact]
    public async Task OtherRenewalError_IsNotHidden()
    {
        _graph.Existing.Add(Subscription("sub-1", "communications/callRecords", NotificationUrl));
        _graph.RenewError = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<HttpRequestException>(() => _manager.EnsureAsync(CancellationToken.None));

        Assert.Empty(_graph.Created);
    }

    private static GraphSubscription Subscription(string id, string resource, string notificationUrl) =>
        new(id, resource, "updated", notificationUrl, null, Now.AddDays(1), null);

    private sealed class FakeGraph : IGraphClient
    {
        public List<GraphSubscription> Existing { get; } = [];
        public List<GraphSubscription> Created { get; } = [];
        public List<(string Id, DateTimeOffset Expiration)> Renewed { get; } = [];
        public Exception? RenewError { get; set; }

        public Task<IReadOnlyList<GraphSubscription>> ListSubscriptionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphSubscription>>(Existing);

        public Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscription subscription, CancellationToken ct)
        {
            Created.Add(subscription);
            return Task.FromResult(subscription with { Id = "new" });
        }

        public Task RenewSubscriptionAsync(string id, DateTimeOffset expiration, CancellationToken ct)
        {
            if (RenewError is not null)
                throw RenewError;
            Renewed.Add((id, expiration));
            return Task.CompletedTask;
        }

        public Task<GraphCallRecord> GetCallRecordAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
