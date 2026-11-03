using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceBot;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Graph;
using VoiceBot.Services;
using Xunit;

namespace VoiceBot.Tests;

public sealed class CallRecordProcessorTests : IDisposable
{
    private const string CallId = "call-1";
    private const long ChatId = 42;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeGraph _graph = new();
    private readonly FakeBot _bot = new();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly CallRecordProcessor _processor;

    public CallRecordProcessorTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _processor = new CallRecordProcessor(
            _db, _graph, _bot, Options.Create(new GraphOptions { NotifyChatId = ChatId }), _time,
            NullLogger<CallRecordProcessor>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task NothingPending_ReturnsFalse()
    {
        Assert.False(await _processor.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NewRecord_IsSaved_SummarySent_AndPendingRemoved()
    {
        _graph.Record = PoorCall(version: 1);
        await AddPendingAsync();

        Assert.True(await _processor.ProcessNextAsync(CancellationToken.None));

        var saved = await _db.CallRecords.AsNoTracking().SingleAsync();
        Assert.Equal(CallQuality.Poor, saved.Quality);
        Assert.Equal(192, saved.DurationSeconds);
        Assert.Equal("Partner One, Partner Two", saved.Participants);
        Assert.Equal(2, saved.AudioStreams);
        Assert.Equal(1, saved.PoorStreams);
        Assert.Empty(await _db.PendingCallRecords.ToListAsync());

        var message = Assert.Single(_bot.Sent);
        Assert.Equal(ChatId, message.ChatId);
        Assert.Contains("🔴", message.Text);
        Assert.Contains("640 мс", message.Text);
        Assert.Contains("12%", message.Text);
    }

    [Fact]
    public async Task OnlyMainAudioStreams_AreClassified()
    {
        _graph.Record = PoorCall(version: 1, label: "main-video");
        await AddPendingAsync();

        await _processor.ProcessNextAsync(CancellationToken.None);

        var saved = await _db.CallRecords.AsNoTracking().SingleAsync();
        Assert.Equal(CallQuality.Unclassified, saved.Quality);
        Assert.Equal(0, saved.AudioStreams);
    }

    [Fact]
    public async Task RepeatedNotification_SameVersion_SendsNoSecondMessage()
    {
        _graph.Record = PoorCall(version: 1);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        Assert.Single(await _db.CallRecords.ToListAsync());
        Assert.Single(_bot.Sent);
        Assert.Empty(await _db.PendingCallRecords.ToListAsync());
    }

    [Fact]
    public async Task NewVersion_SameQuality_UpdatesRecord_WithoutMessage()
    {
        _graph.Record = PoorCall(version: 1);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        _graph.Record = PoorCall(version: 2);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(2, (await _db.CallRecords.AsNoTracking().SingleAsync()).Version);
        Assert.Single(_bot.Sent);
    }

    [Fact]
    public async Task NewVersion_QualityChanged_SendsMessage()
    {
        _graph.Record = GoodCall(version: 1);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        _graph.Record = PoorCall(version: 2);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(CallQuality.Poor, (await _db.CallRecords.AsNoTracking().SingleAsync()).Quality);
        Assert.Equal(2, _bot.Sent.Count);
        Assert.Contains("🟢", _bot.Sent[0].Text);
        Assert.Contains("🔴", _bot.Sent[1].Text);
    }

    [Fact]
    public async Task OlderVersion_DoesNotOverwriteNewerRecord()
    {
        _graph.Record = PoorCall(version: 5);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        _graph.Record = GoodCall(version: 4);
        await AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);

        var saved = await _db.CallRecords.AsNoTracking().SingleAsync();
        Assert.Equal(5, saved.Version);
        Assert.Equal(CallQuality.Poor, saved.Quality);
    }

    [Fact]
    public async Task TelegramFailure_DoesNotLoseTheRecord_AndDoesNotRetry()
    {
        _graph.Record = PoorCall(version: 1);
        _bot.Fail = true;
        await AddPendingAsync();

        await _processor.ProcessNextAsync(CancellationToken.None);

        Assert.Single(await _db.CallRecords.ToListAsync());
        Assert.Empty(await _db.PendingCallRecords.ToListAsync());
    }

    [Fact]
    public async Task GraphFailure_KeepsPending_AndSchedulesARetry()
    {
        _graph.Error = new HttpRequestException("not ready", null, HttpStatusCode.NotFound);
        await AddPendingAsync();

        await _processor.ProcessNextAsync(CancellationToken.None);

        var pending = await _db.PendingCallRecords.AsNoTracking().SingleAsync();
        Assert.Equal(1, pending.Attempts);
        Assert.False(pending.GaveUp);
        Assert.Equal("not ready", pending.LastError);
        Assert.Equal(_time.Now.UtcDateTime.AddMinutes(1), pending.NextAttemptUtc);

        // Not due yet.
        Assert.False(await _processor.ProcessNextAsync(CancellationToken.None));

        _time.Now = _time.Now.AddMinutes(1);
        Assert.True(await _processor.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(2, (await _db.PendingCallRecords.AsNoTracking().SingleAsync()).Attempts);
    }

    [Fact]
    public async Task RepeatedFailures_GiveUp_AndStopRetrying()
    {
        _graph.Error = new HttpRequestException("down");
        await AddPendingAsync();

        for (var attempt = 0; attempt < CallRecordProcessor.MaxAttempts; attempt++)
        {
            _time.Now = _time.Now.AddDays(1);
            Assert.True(await _processor.ProcessNextAsync(CancellationToken.None));
        }

        Assert.True((await _db.PendingCallRecords.AsNoTracking().SingleAsync()).GaveUp);
        _time.Now = _time.Now.AddDays(1);
        Assert.False(await _processor.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NotificationArrivingDuringProcessing_IsNotLost()
    {
        _graph.Record = PoorCall(version: 1);
        await AddPendingAsync();

        // Graph reports a new version while the processor is reading the previous one.
        _graph.OnGet = () => AddPendingAsync();
        await _processor.ProcessNextAsync(CancellationToken.None);
        _graph.OnGet = null;

        var pending = await _db.PendingCallRecords.AsNoTracking().SingleAsync();
        Assert.Equal(2, pending.Revision);

        _graph.Record = PoorCall(version: 2);
        await _processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(2, (await _db.CallRecords.AsNoTracking().SingleAsync()).Version);
        Assert.Empty(await _db.PendingCallRecords.ToListAsync());
    }

    [Fact]
    public async Task NoChatConfigured_StillSavesTheRecord()
    {
        var processor = new CallRecordProcessor(
            _db, _graph, _bot, Options.Create(new GraphOptions()), _time, NullLogger<CallRecordProcessor>.Instance);
        _graph.Record = PoorCall(version: 1);
        await AddPendingAsync();

        await processor.ProcessNextAsync(CancellationToken.None);

        Assert.Single(await _db.CallRecords.ToListAsync());
        Assert.Empty(_bot.Sent);
    }

    private Task AddPendingAsync()
    {
        var now = _time.Now.UtcDateTime;
        return _db.Database.ExecuteSqlAsync($"""
            INSERT INTO PendingCallRecords (Id, Revision, Attempts, GaveUp, ReceivedAtUtc, NextAttemptUtc)
            VALUES ({CallId}, 1, 0, 0, {now}, {now})
            ON CONFLICT (Id) DO UPDATE SET Revision = Revision + 1, Attempts = 0, GaveUp = 0, NextAttemptUtc = {now}
            """);
    }

    private static GraphCallRecord GoodCall(long version) => Call(version, "main-audio", poor: false);

    private static GraphCallRecord PoorCall(long version, string label = "main-audio") => Call(version, label, poor: true);

    private static GraphCallRecord Call(long version, string label, bool poor)
    {
        var start = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var good = new GraphStream("calleeToCaller", 9600, 0.001, TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(8));
        var second = poor
            ? new GraphStream("callerToCallee", 9600, 0.12, TimeSpan.FromMilliseconds(640), TimeSpan.FromMilliseconds(45))
            : good;

        return new GraphCallRecord(CallId, version, "peerToPeer", ["audio"], start, start.AddSeconds(192))
        {
            Sessions = [new GraphSession([new GraphSegment([new GraphMedia(label, [good, second])])])],
            Participants =
            [
                new GraphParticipant("a", new GraphIdentitySet(new GraphIdentity("a", "Partner One"), null, null, null)),
                new GraphParticipant("b", new GraphIdentitySet(new GraphIdentity("b", "Partner Two"), null, null, null))
            ]
        };
    }

    private sealed class FakeGraph : IGraphClient
    {
        public GraphCallRecord? Record { get; set; }
        public Exception? Error { get; set; }
        public Func<Task>? OnGet { get; set; }

        public async Task<GraphCallRecord> GetCallRecordAsync(string id, CancellationToken ct)
        {
            if (OnGet is not null)
                await OnGet();
            if (Error is not null)
                throw Error;
            return Record!;
        }

        public Task<IReadOnlyList<GraphSubscription>> ListSubscriptionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphSubscription> CreateSubscriptionAsync(GraphSubscription subscription, CancellationToken ct) => throw new NotSupportedException();
        public Task RenewSubscriptionAsync(string id, DateTimeOffset expiration, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeBot : IBotGateway
    {
        public List<(long ChatId, string Text)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task<int> SendAsync(long chatId, string text, CancellationToken ct)
        {
            if (Fail)
                throw new HttpRequestException("telegram is down");
            Sent.Add((chatId, text));
            return Task.FromResult(1);
        }

        public Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task AnswerCallbackAsync(string callbackId, CancellationToken ct) => Task.CompletedTask;
        public Task EditAsync(long chatId, int messageId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct) => Task.CompletedTask;
    }
}
