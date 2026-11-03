using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VoiceBot.Services;

public sealed class WhisperClient(HttpClient http, ILogger<WhisperClient> logger) : ITranscriber
{
    private const int MaxAttempts = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<string> TranscribeAsync(string wavPath, string language, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostAsync(wavPath, language, ct);
            }
            // No status code means the server was not reached, so nothing was processed and a retry is safe.
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Whisper is unreachable, attempt {Attempt} of {MaxAttempts}", attempt, MaxAttempts);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<string> PostAsync(string wavPath, string language, CancellationToken ct)
    {
        await using var file = File.OpenRead(wavPath);
        using var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var form = new MultipartFormDataContent
        {
            { fileContent, "file", Path.GetFileName(wavPath) },
            { new StringContent(language), "language" }
        };

        using var response = await http.PostAsync("/inference", form, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<InferenceResponse>(ct);
        return JoinSegments(result?.Text);
    }

    // Whisper puts each recognized segment on its own line, often in the middle of a sentence.
    private static string JoinSegments(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record InferenceResponse([property: JsonPropertyName("text")] string? Text);
}
