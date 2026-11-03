using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using VoiceBot.Graph;
using Xunit;

namespace VoiceBot.Tests;

public sealed class StartupTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"voicebot-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    // docker-compose.yml passes every GRAPH_* value, and they are empty on a server without Teams.
    [Fact]
    public async Task EmptyGraphValuesFromCompose_DoNotStopTheBot_AndWebhooksAreOff()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
            builder.UseSetting("Telegram:Enabled", "false");
            builder.UseSetting("Graph:Enabled", "false");
            builder.UseSetting("Graph:TenantId", "");
            builder.UseSetting("Graph:ClientId", "");
            builder.UseSetting("Graph:ClientSecret", "");
            builder.UseSetting("Graph:ClientState", "");
            builder.UseSetting("Graph:PublicBaseUrl", "");
            builder.UseSetting("Graph:NotifyChatId", "");
        });
        using var http = factory.CreateClient();

        using var health = await http.GetAsync("/health");
        using var webhook = await http.PostAsync(GraphWebhookEndpoints.NotificationsPath, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, webhook.StatusCode);
    }

    [Fact]
    public void GraphEnabledWithoutSettings_FailsAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
            builder.UseSetting("Telegram:Enabled", "false");
            builder.UseSetting("Graph:Enabled", "true");
        });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
