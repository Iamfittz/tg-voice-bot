using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using VoiceBot;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Services;
using VoiceBot.Telegram;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TelegramOptions>().BindConfiguration("Telegram").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<WhisperOptions>().BindConfiguration("Whisper").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LimitOptions>().BindConfiguration("Limits").ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Created directly, not through IHttpClientFactory: its logging would write request URLs,
// and Telegram URLs contain the bot token.
builder.Services.AddSingleton<ITelegramBotClient>(services =>
    new TelegramBotClient(services.GetRequiredService<IOptions<TelegramOptions>>().Value.Token));

builder.Services.AddHttpClient<WhisperClient>((services, http) =>
{
    http.BaseAddress = new Uri(services.GetRequiredService<IOptions<WhisperOptions>>().Value.BaseUrl);
    // The whole job is limited by LimitOptions.JobTimeoutSeconds through the cancellation token.
    http.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddTransient<ITranscriber>(services => services.GetRequiredService<WhisperClient>());
builder.Services.AddSingleton<IAudioConverter, FfmpegAudioConverter>();
builder.Services.AddSingleton<IBotGateway, TelegramGateway>();
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddScoped<TranscriptionProcessor>();
builder.Services.AddScoped<UpdateRouter>();
builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddHostedService<TelegramPollingService>();

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

app.Run();

internal sealed record StatsResponse(int Total, int Completed, int Failed, int AudioSeconds, double? AverageProcessingMs);
