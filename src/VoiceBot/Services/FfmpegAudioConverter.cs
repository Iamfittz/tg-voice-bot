using System.Diagnostics;

namespace VoiceBot.Services;

public sealed class FfmpegAudioConverter : IAudioConverter
{
    public async Task ConvertToWavAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        // Arguments go through ArgumentList, not a shell string, so paths cannot inject anything.
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", inputPath,
            "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
            outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {errors.Trim()}");
    }
}
