namespace VoiceBot.Services;

public interface IBotGateway
{
    Task<int> SendAsync(long chatId, string text, CancellationToken ct);
    Task SendLanguagePickerAsync(long chatId, string text, CancellationToken ct);
    Task AnswerCallbackAsync(string callbackId, CancellationToken ct);
    Task EditAsync(long chatId, int messageId, string text, CancellationToken ct);
    Task DownloadAsync(string fileId, string destinationPath, CancellationToken ct);
}

public interface IAudioConverter
{
    Task ConvertToWavAsync(string inputPath, string outputPath, CancellationToken ct);
}

public interface ITranscriber
{
    Task<string> TranscribeAsync(string wavPath, string language, CancellationToken ct);
}
