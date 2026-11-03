using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceBot.Services;
using Xunit;

namespace VoiceBot.Tests;

public sealed class WhisperClientTests : IDisposable
{
    private readonly string _wavPath = Path.GetTempFileName();

    public void Dispose() => File.Delete(_wavPath);

    [Fact]
    public async Task ParsesText_AndSendsFileAndLanguage()
    {
        string? body = null;
        var client = CreateClient(new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json("""{"text":"  hello \n"}""");
        }));

        var text = await client.TranscribeAsync(_wavPath, "uk", CancellationToken.None);

        Assert.Equal("hello", text);
        Assert.Contains("name=file", body);
        Assert.Contains("name=language", body);
        Assert.Contains("uk", body);
    }

    [Fact]
    public async Task JoinsWhisperSegmentsIntoOneParagraph()
    {
        var client = CreateClient(new StubHandler(_ =>
            Task.FromResult(Json("""{"text":" Перша частина\n другого речення,\n і кінець.\n"}"""))));

        var text = await client.TranscribeAsync(_wavPath, "uk", CancellationToken.None);

        Assert.Equal("Перша частина другого речення, і кінець.", text);
    }

    [Fact]
    public async Task RetriesWhenServerIsUnreachable()
    {
        var calls = 0;
        var client = CreateClient(new StubHandler(_ =>
        {
            if (++calls < 3)
                throw new HttpRequestException("connection refused");
            return Task.FromResult(Json("""{"text":"ok"}"""));
        }));

        Assert.Equal("ok", await client.TranscribeAsync(_wavPath, "uk", CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GivesUpAfterThreeAttempts()
    {
        var calls = 0;
        var client = CreateClient(new StubHandler(_ =>
        {
            calls++;
            throw new HttpRequestException("connection refused");
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.TranscribeAsync(_wavPath, "uk", CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DoesNotRetryServerError()
    {
        var calls = 0;
        var client = CreateClient(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.TranscribeAsync(_wavPath, "uk", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    private static WhisperClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://whisper.test") },
            NullLogger<WhisperClient>.Instance)
        {
            RetryDelay = TimeSpan.Zero
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
