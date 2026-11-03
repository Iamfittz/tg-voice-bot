using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using VoiceBot.Data;
using VoiceBot.Domain;
using VoiceBot.Services;
using VoiceBot.Telegram;
using Xunit;

namespace VoiceBot.Tests;

public sealed class UpdateRouterTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeBot _bot = new();
    private readonly TranscriptionQueue _queue = new(Options.Create(new LimitOptions()));
    private readonly UpdateRouter _router;

    public UpdateRouterTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _router = new UpdateRouter(
            _db, _bot, _queue, Options.Create(new LimitOptions()),
            Options.Create(new WhisperOptions { BaseUrl = "http://whisper.test", Language = "uk" }));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData(null)]
    public async Task Voice_WithoutSavedChoice_UsesDefault_NotTelegramLanguage(string? telegramLanguage)
    {
        await _router.HandleAsync(Voice(telegramLanguage), CancellationToken.None);

        Assert.Equal("uk", (await NextJobAsync()).Language);
    }

    [Fact]
    public async Task SavedLanguage_WinsOverDefault()
    {
        await _router.HandleAsync(Text("/ru"), CancellationToken.None);
        await _router.HandleAsync(Voice("uk"), CancellationToken.None);

        Assert.Equal("ru", (await NextJobAsync()).Language);
        Assert.Equal("ru", (await _db.Transcriptions.SingleAsync()).Language);
    }

    [Fact]
    public async Task LanguageCanBeChangedAgain_AndCommandMayHaveBotName()
    {
        await _router.HandleAsync(Text("/uk"), CancellationToken.None);
        await _router.HandleAsync(Text("/EN@my_bot"), CancellationToken.None);

        Assert.Equal("en", (await _db.UserSettings.AsNoTracking().SingleAsync()).Language);
        Assert.Equal(BotTexts.LanguageChanged("англійська"), _bot.Sent[^1]);
    }

    [Fact]
    public async Task Forget_RemovesTranscriptionsAndSettings_OfThisUserOnly()
    {
        await _router.HandleAsync(Text("/uk"), CancellationToken.None);
        await _router.HandleAsync(Voice("uk"), CancellationToken.None);
        _db.Transcriptions.Add(new Transcription { TelegramUserId = 999, ChatId = 999 });
        await _db.SaveChangesAsync();

        await _router.HandleAsync(Text("/forget"), CancellationToken.None);

        Assert.Equal(999, (await _db.Transcriptions.AsNoTracking().SingleAsync()).TelegramUserId);
        Assert.Empty(await _db.UserSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TooLongVoice_IsRejected_WithoutQueueing()
    {
        await _router.HandleAsync(Voice("uk", durationSeconds: 301), CancellationToken.None);

        Assert.Equal(BotTexts.TooLong(300), _bot.Sent.Single());
        Assert.Empty(await _db.Transcriptions.ToListAsync());
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/unknown")]
    [InlineData("hello")]
    public async Task OtherText_GetsWelcomeAndLanguageButtons(string text)
    {
        await _router.HandleAsync(Text(text), CancellationToken.None);

        Assert.Equal(BotTexts.Welcome, _bot.Sent.Single());
        Assert.Equal(BotTexts.ChooseLanguage, _bot.Pickers.Single());
    }

    [Fact]
    public async Task LangCommand_ShowsCurrentLanguageAndButtons()
    {
        await _router.HandleAsync(Text("/lang"), CancellationToken.None);

        Assert.Equal(BotTexts.CurrentLanguage("українська"), _bot.Pickers.Single());
        Assert.Empty(_bot.Sent);
    }

    [Fact]
    public async Task LanguageButton_SavesChoice_AnswersCallback_AndUpdatesMessage()
    {
        await _router.HandleCallbackAsync(Callback("lang:ru"), CancellationToken.None);
        await _router.HandleAsync(Voice("uk"), CancellationToken.None);

        Assert.Equal("cb", _bot.AnsweredCallbacks.Single());
        Assert.Equal(BotTexts.LanguageChanged("російська"), _bot.Edits.Single());
        Assert.Equal("ru", (await NextJobAsync()).Language);
    }

    [Theory]
    [InlineData("lang:de")]
    [InlineData("lang:")]
    [InlineData("other")]
    [InlineData(null)]
    public async Task UnknownCallbackData_IsIgnored_ButStillAnswered(string? data)
    {
        await _router.HandleCallbackAsync(Callback(data), CancellationToken.None);

        Assert.Equal("cb", _bot.AnsweredCallbacks.Single());
        Assert.Empty(await _db.UserSettings.ToListAsync());
        Assert.Empty(_bot.Edits);
    }

    private async Task<TranscriptionJob> NextJobAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var job in _queue.ReadAllAsync(timeout.Token))
            return job;
        throw new InvalidOperationException("No job in the queue");
    }

    private static CallbackQuery Callback(string? data) => new()
    {
        Id = "cb",
        From = new User { Id = 1 },
        Message = new Message { Chat = new Chat { Id = 2 } },
        Data = data
    };

    private static Message Text(string text) => new()
    {
        From = new User { Id = 1, LanguageCode = "en" },
        Chat = new Chat { Id = 2 },
        Text = text
    };

    private static Message Voice(string? telegramLanguage, int durationSeconds = 2) => new()
    {
        From = new User { Id = 1, LanguageCode = telegramLanguage },
        Chat = new Chat { Id = 2 },
        Voice = new Voice { FileId = "file-id", Duration = durationSeconds, FileSize = 100 }
    };

    private sealed class FakeBot : IBotGateway
    {
        public List<string> Sent { get; } = [];
        public List<string> Pickers { get; } = [];
        public List<string> Edits { get; } = [];
        public List<string> AnsweredCallbacks { get; } = [];

        public Task<int> SendAsync(long chatId, string text, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.FromResult(1);
        }

        public Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct)
        {
            Pickers.Add(text);
            return Task.CompletedTask;
        }

        public Task AnswerCallbackAsync(string callbackId, CancellationToken ct)
        {
            AnsweredCallbacks.Add(callbackId);
            return Task.CompletedTask;
        }

        public Task EditAsync(long chatId, int messageId, string text, CancellationToken ct)
        {
            Edits.Add(text);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct) => Task.CompletedTask;
    }
}
