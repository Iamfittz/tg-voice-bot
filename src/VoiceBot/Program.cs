using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using VoiceBot;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Graph;
using VoiceBot.Services;
using VoiceBot.Telegram;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TelegramOptions>().BindConfiguration("Telegram")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Token), "Telegram:Token is required when Telegram is enabled")
    .ValidateOnStart();
builder.Services.AddOptions<GraphOptions>().BindConfiguration("Graph").ValidateDataAnnotations()
    .Validate(o => !o.Enabled || o.IsComplete, "Graph needs TenantId, ClientId, ClientSecret, PublicBaseUrl and ClientState")
    .ValidateOnStart();
builder.Services.AddOptions<WhisperOptions>().BindConfiguration("Whisper").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LimitOptions>().BindConfiguration("Limits").ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

if (builder.Configuration.GetValue("Telegram:Enabled", true))
{
    // Created directly, not through IHttpClientFactory: its logging would write request URLs,
    // and Telegram URLs contain the bot token.
    builder.Services.AddSingleton<ITelegramBotClient>(services =>
        new TelegramBotClient(services.GetRequiredService<IOptions<TelegramOptions>>().Value.Token));
    builder.Services.AddSingleton<IBotGateway, TelegramGateway>();
    builder.Services.AddHostedService<TelegramPollingService>();
}
else
{
    builder.Services.AddSingleton<IBotGateway, LogOnlyBotGateway>();
}

var graphEnabled = builder.Configuration.GetValue("Graph:Enabled", false);
if (graphEnabled)
{
    builder.Services.AddHttpClient(GraphTokenProvider.HttpClientName);
    builder.Services.AddSingleton<GraphTokenProvider>();
    builder.Services.AddSingleton<GraphSubscriptionSignal>();
    builder.Services.AddHttpClient<IGraphClient, GraphClient>((services, http) =>
        http.BaseAddress = new Uri(services.GetRequiredService<IOptions<GraphOptions>>().Value.GraphBaseUrl.TrimEnd('/') + "/"));
    builder.Services.AddScoped<GraphSubscriptionManager>();
    builder.Services.AddScoped<CallRecordProcessor>();
    builder.Services.AddHostedService<GraphSubscriptionService>();
    builder.Services.AddHostedService<CallRecordWorker>();
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<WhisperClient>((services, http) =>
{
    http.BaseAddress = new Uri(services.GetRequiredService<IOptions<WhisperOptions>>().Value.BaseUrl);
    // The whole job is limited by LimitOptions.JobTimeoutSeconds through the cancellation token.
    http.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddTransient<ITranscriber>(services => services.GetRequiredService<WhisperClient>());
builder.Services.AddSingleton<IAudioConverter, FfmpegAudioConverter>();
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddScoped<TranscriptionProcessor>();
builder.Services.AddScoped<UpdateRouter>();
builder.Services.AddHostedService<TranscriptionWorker>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("readiness");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

if (graphEnabled)
    app.MapGraphWebhooks();

var api = app.MapGroup("/api");

api.MapGet("/transcriptions", async (AppDbContext db, long? userId, int limit = 20, int offset = 0) =>
{
    var query = db.Transcriptions.AsNoTracking();
    if (userId is not null)
        query = query.Where(t => t.TelegramUserId == userId);

    return await query
        .OrderByDescending(t => t.Id)
        .Skip(Math.Max(offset, 0))
        .Take(Math.Clamp(limit, 1, 100))
        .ToListAsync();
});

api.MapGet("/transcriptions/{id:long}", async (AppDbContext db, long id) =>
    await db.Transcriptions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) is { } transcription
        ? Results.Ok(transcription)
        : Results.NotFound());

api.MapGet("/stats", async (AppDbContext db) =>
{
    var completed = db.Transcriptions.AsNoTracking().Where(t => t.Status == TranscriptionStatus.Completed);
    return new StatsResponse(
        await db.Transcriptions.CountAsync(),
        await completed.CountAsync(),
        await db.Transcriptions.CountAsync(t => t.Status == TranscriptionStatus.Failed),
        await completed.SumAsync(t => t.DurationSeconds),
        await completed.AverageAsync(t => (double?)t.ProcessingMs));
});

api.MapGet("/calls", async (AppDbContext db, CallQuality? quality, int limit = 20, int offset = 0) =>
{
    var query = db.CallRecords.AsNoTracking();
    if (quality is not null)
        query = query.Where(c => c.Quality == quality);

    return await query
        .OrderByDescending(c => c.UpdatedAtUtc)
        .Skip(Math.Max(offset, 0))
        .Take(Math.Clamp(limit, 1, 100))
        .ToListAsync();
});

api.MapGet("/calls/{id}", async (AppDbContext db, string id) =>
    await db.CallRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) is { } call
        ? Results.Ok(call)
        : Results.NotFound());

app.Run();

internal sealed record StatsResponse(int Total, int Completed, int Failed, int AudioSeconds, double? AverageProcessingMs);

public partial class Program;
