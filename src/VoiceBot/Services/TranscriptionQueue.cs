using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace VoiceBot.Services;

public sealed record TranscriptionJob(long TranscriptionId, long ChatId, string FileId, string Language, int ProgressMessageId);

public sealed class TranscriptionQueue(IOptions<LimitOptions> options)
{
    private readonly int _capacity = options.Value.QueueCapacity;

    private readonly Channel<TranscriptionJob> _channel = Channel.CreateBounded<TranscriptionJob>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public bool HasSpace => _channel.Reader.Count < _capacity;

    public bool TryEnqueue(TranscriptionJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<TranscriptionJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
