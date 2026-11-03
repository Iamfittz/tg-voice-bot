using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Services;
using Xunit;

namespace VoiceBot.Tests;

public sealed class TranscriptionProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeBot _bot = new();
    private readonly FakeConverter _converter = new();
    private readonly LimitOptions _limits = new() { JobTimeoutSeconds = 30 };

    public TranscriptionProcessorTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Success_SavesText_RepliesToUser_AndRemovesTempFiles()
    {
        var record = await AddRecordAsync();

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => Task.FromResult("hello world")));

        Assert.Equal(TranscriptionStatus.Completed, record.Status);
        Assert.Equal("hello world", record.Text);
        Assert.NotNull(record.ProcessingMs);
        Assert.Equal(["hello world"], _bot.Edits);
        Assert.False(Directory.Exists(Path.GetDirectoryName(_bot.DownloadedTo)));
    }

    [Fact]
    public async Task PassesJobLanguageToTranscriber()
    {
        var record = await AddRecordAsync();
        string? language = null;

        await ProcessAsync(record, new FakeTranscriber((_, lang, _) =>
        {
            language = lang;
            return Task.FromResult("text");
        }));

        Assert.Equal("uk", language);
    }

    [Fact]
    public async Task EmptyTranscription_TellsUserThereIsNoSpeech()
    {
        var record = await AddRecordAsync();

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => Task.FromResult("")));

        Assert.Equal(TranscriptionStatus.Completed, record.Status);
        Assert.Equal([BotTexts.NoSpeech], _bot.Edits);
    }

    [Fact]
    public async Task TranscriberFailure_MarksFailed_AndTellsUser()
    {
        var record = await AddRecordAsync();

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => throw new HttpRequestException("whisper is down")));

        Assert.Equal(TranscriptionStatus.Failed, record.Status);
        Assert.Equal("whisper is down", record.Error);
        Assert.Equal([BotTexts.Failed], _bot.Edits);
    }

    [Fact]
    public async Task HangingTranscriber_IsStoppedByJobTimeout()
    {
        _limits.JobTimeoutSeconds = 1;
        var record = await AddRecordAsync();

        await ProcessAsync(record, new FakeTranscriber(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }));

        Assert.Equal(TranscriptionStatus.Failed, record.Status);
        Assert.Equal("Timed out", record.Error);
    }

    [Fact]
    public async Task LongTranscription_IsDeliveredInSeveralMessages()
    {
        var record = await AddRecordAsync();
        var text = string.Join(' ', Enumerable.Repeat("слово", 2000));

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => Task.FromResult(text)));

        Assert.Single(_bot.Edits);
        Assert.NotEmpty(_bot.Sent);
        Assert.Equal(text, string.Join(' ', _bot.Edits.Concat(_bot.Sent)));
    }

    [Fact]
    public async Task TelegramFailure_DoesNotLoseFinishedTranscription()
    {
        var record = await AddRecordAsync();
        _bot.FailOnEdit = true;

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => Task.FromResult("hello")));

        Assert.Equal(TranscriptionStatus.Completed, record.Status);
        Assert.Equal("hello", record.Text);
    }

    [Fact]
    public async Task RecordDeletedByForget_IsSkipped()
    {
        var record = await AddRecordAsync();
        _db.Transcriptions.Remove(record);
        await _db.SaveChangesAsync();

        await ProcessAsync(record, new FakeTranscriber((_, _, _) => throw new InvalidOperationException("must not run")));

        Assert.Empty(_bot.Edits);
        Assert.Null(_bot.DownloadedTo);
    }

    [Fact]
    public async Task Initializer_FailsJobsInterruptedByRestart()
    {
        var record = await AddRecordAsync();

        await DatabaseInitializer.InitializeAsync(_db);
        await _db.Entry(record).ReloadAsync();

        Assert.Equal(TranscriptionStatus.Failed, record.Status);
        Assert.Equal(DatabaseInitializer.InterruptedError, record.Error);
    }

    private async Task<Transcription> AddRecordAsync()
    {
        var record = new Transcription { TelegramUserId = 1, ChatId = 2, DurationSeconds = 5 };
        _db.Transcriptions.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    private Task ProcessAsync(Transcription record, ITranscriber transcriber)
    {
        var processor = new TranscriptionProcessor(
            _db, _bot, _converter, transcriber, Options.Create(_limits),
            NullLogger<TranscriptionProcessor>.Instance);
        return processor.ProcessAsync(new TranscriptionJob(record.Id, record.ChatId, "file-id", "uk", 10), CancellationToken.None);
    }

    private sealed class FakeBot : IBotGateway
    {
        public List<string> Edits { get; } = [];
        public List<string> Sent { get; } = [];
        public bool FailOnEdit { get; set; }
        public string? DownloadedTo { get; private set; }

        public Task<int> SendAsync(long chatId, string text, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.FromResult(1);
        }

        public Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct) => Task.CompletedTask;

        public Task AnswerCallbackAsync(string callbackId, CancellationToken ct) => Task.CompletedTask;

        public Task EditAsync(long chatId, int messageId, string text, CancellationToken ct)
        {
            if (FailOnEdit)
                throw new HttpRequestException("telegram is down");
            Edits.Add(text);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct)
        {
            DownloadedTo = destinationPath;
            return File.WriteAllTextAsync(destinationPath, "audio", ct);
        }
    }

    private sealed class FakeConverter : IAudioConverter
    {
        public Task ConvertToWavAsync(string inputPath, string outputPath, CancellationToken ct) =>
            File.WriteAllTextAsync(outputPath, "wav", ct);
    }

    private sealed class FakeTranscriber(Func<string, string, CancellationToken, Task<string>> transcribe) : ITranscriber
    {
        public Task<string> TranscribeAsync(string wavPath, string language, CancellationToken ct) =>
            transcribe(wavPath, language, ct);
    }
}
