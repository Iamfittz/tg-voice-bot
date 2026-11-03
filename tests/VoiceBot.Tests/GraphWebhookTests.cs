using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Graph;
using Xunit;

namespace VoiceBot.Tests;

public sealed class GraphWebhookTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private const string Secret = "client-state-secret";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"voicebot-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public GraphWebhookTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
            builder.UseSetting("Telegram:Enabled", "false");
            builder.UseSetting("Graph:Enabled", "true");
            builder.UseSetting("Graph:TenantId", Tenant);
            builder.UseSetting("Graph:ClientId", "client");
            builder.UseSetting("Graph:ClientSecret", "secret");
            builder.UseSetting("Graph:ClientState", Secret);
            builder.UseSetting("Graph:PublicBaseUrl", "https://bot.test");
            builder.ConfigureTestServices(services =>
            {
                // The tests look at what the webhook stored, so nothing may process it in the background.
                var background = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                        && (d.ImplementationType == typeof(CallRecordWorker) || d.ImplementationType == typeof(GraphSubscriptionService)))
                    .ToList();
                foreach (var descriptor in background)
                    services.Remove(descriptor);
            });
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Theory]
    [InlineData(GraphWebhookEndpoints.NotificationsPath)]
    [InlineData(GraphWebhookEndpoints.LifecyclePath)]
    public async Task ValidationToken_IsReturnedDecoded_AsPlainText(string path)
    {
        const string token = "Validation: Testing client application reachability & more";

        using var response = await _http.PostAsync(
            $"{path}?validationToken={Uri.EscapeDataString(token)}",
            new StringContent("", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(token, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthenticNotification_IsStored_AndAccepted()
    {
        using var response = await PostAsync(Notification("call-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("call-1", pending.Id);
        Assert.Equal(1, pending.Revision);
    }

    [Fact]
    public async Task BatchWithSeveralCalls_StoresEachOfThem()
    {
        using var response = await PostAsync(Notification("call-1"), Notification("call-2"));

        Assert.Equal(["call-1", "call-2"], (await PendingAsync()).Select(p => p.Id).Order());
    }

    [Fact]
    public async Task SameCallAgain_RaisesRevision_AndResetsARetry()
    {
        (await PostAsync(Notification("call-1"))).Dispose();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PendingCallRecords.ExecuteUpdateAsync(s => s.SetProperty(p => p.Attempts, 7).SetProperty(p => p.GaveUp, true));
        }

        (await PostAsync(Notification("call-1"))).Dispose();

        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(2, pending.Revision);
        Assert.Equal(0, pending.Attempts);
        Assert.False(pending.GaveUp);
    }

    [Theory]
    [InlineData("wrong-secret", Tenant)]
    [InlineData(null, Tenant)]
    [InlineData(Secret, "other-tenant")]
    [InlineData(Secret, null)]
    public async Task ForgedNotification_IsNotStored(string? clientState, string? tenant)
    {
        using var response = await PostAsync(new { clientState, tenantId = tenant, resourceData = new { id = "call-1" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task OneForgedItem_DoesNotBlockAnAuthenticOne()
    {
        using var response = await PostAsync(
            new { clientState = "wrong", tenantId = Tenant, resourceData = new { id = "forged" } },
            Notification("real"));

        Assert.Equal("real", Assert.Single(await PendingAsync()).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task NotificationWithoutId_IsIgnored(string? id)
    {
        using var response = await PostAsync(Notification(id));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task TooLongId_IsIgnored()
    {
        using var response = await PostAsync(Notification(new string('a', 129)));

        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task InvalidJson_IsBadRequest()
    {
        using var response = await _http.PostAsync(
            GraphWebhookEndpoints.NotificationsPath, new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CallsApi_ReturnsSavedRecords_FilteredByQuality()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CallRecords.Add(new CallRecordEntry { Id = "poor", Quality = CallQuality.Poor });
            db.CallRecords.Add(new CallRecordEntry { Id = "good", Quality = CallQuality.Good });
            await db.SaveChangesAsync();
        }

        var calls = await _http.GetFromJsonAsync<JsonElement>("/api/calls?quality=Poor");
        var call = await _http.GetFromJsonAsync<JsonElement>("/api/calls/good");
        using var missing = await _http.GetAsync("/api/calls/none");

        Assert.Equal("poor", calls.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal("Good", call.GetProperty("quality").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_ReauthorizationRequired_RequestsARenewal()
    {
        var signal = _factory.Services.GetRequiredService<GraphSubscriptionSignal>();

        using var response = await _http.PostAsJsonAsync(GraphWebhookEndpoints.LifecyclePath, new
        {
            value = new[] { new { clientState = Secret, tenantId = Tenant, lifecycleEvent = "reauthorizationRequired" } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_ForgedEvent_RequestsNothing()
    {
        var signal = _factory.Services.GetRequiredService<GraphSubscriptionSignal>();

        using var response = await _http.PostAsJsonAsync(GraphWebhookEndpoints.LifecyclePath, new
        {
            value = new[] { new { clientState = "wrong", tenantId = Tenant, lifecycleEvent = "reauthorizationRequired" } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task ExistingDatabase_GetsTheNewTables()
    {
        // A database deployed before call records existed.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE CallRecords; DROP TABLE PendingCallRecords;");
        db.Transcriptions.Add(new Transcription { TelegramUserId = 1, ChatId = 2, Status = TranscriptionStatus.Completed });
        await db.SaveChangesAsync();

        await DatabaseInitializer.InitializeAsync(db);

        db.CallRecords.Add(new CallRecordEntry { Id = "after-upgrade" });
        await db.SaveChangesAsync();
        Assert.Single(await db.CallRecords.ToListAsync());
        Assert.Single(await db.Transcriptions.ToListAsync());
    }

    private static object Notification(string? id) =>
        new { clientState = Secret, tenantId = Tenant, changeType = "updated", resourceData = new { id } };

    private Task<HttpResponseMessage> PostAsync(params object[] notifications) =>
        _http.PostAsJsonAsync(GraphWebhookEndpoints.NotificationsPath, new { value = notifications });

    private async Task<List<PendingCallRecord>> PendingAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PendingCallRecords.AsNoTracking().ToListAsync();
    }
}
