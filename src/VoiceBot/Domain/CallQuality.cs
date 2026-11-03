namespace VoiceBot.Domain;

public enum CallQuality
{
    Unclassified,
    Good,
    Poor
}

public sealed record AudioStreamMetrics(long? Packets, TimeSpan? RoundTrip, double? PacketLossRate, TimeSpan? Jitter);

public sealed record CallQualitySummary(
    CallQuality Quality,
    int AudioStreams,
    int PoorStreams,
    double? MaxRoundTripMs,
    double? MaxPacketLossRate,
    double? MaxJitterMs);

// The audio classifier of the Teams Call Quality Dashboard:
// https://learn.microsoft.com/microsoftteams/stream-classification-in-call-quality-dashboard
public static class CallQualityClassifier
{
    public const long MinPackets = 500;
    public const double MaxRoundTripMs = 500;
    public const double MaxPacketLossRate = 0.1;
    public const double MaxJitterMs = 30;

    public static CallQuality Classify(AudioStreamMetrics stream)
    {
        if (stream.Packets is null or <= MinPackets)
            return CallQuality.Unclassified;

        if (stream.RoundTrip?.TotalMilliseconds > MaxRoundTripMs
            || stream.PacketLossRate > MaxPacketLossRate
            || stream.Jitter?.TotalMilliseconds > MaxJitterMs)
            return CallQuality.Poor;

        return stream.RoundTrip is null || stream.PacketLossRate is null || stream.Jitter is null
            ? CallQuality.Unclassified
            : CallQuality.Good;
    }

    public static CallQualitySummary Summarize(IReadOnlyCollection<AudioStreamMetrics> streams)
    {
        var levels = streams.Select(Classify).ToList();

        // One bad direction is enough: the other participant heard a bad call.
        var quality = levels.Contains(CallQuality.Poor) ? CallQuality.Poor
            : levels.Contains(CallQuality.Good) ? CallQuality.Good
            : CallQuality.Unclassified;

        return new CallQualitySummary(
            quality,
            streams.Count,
            levels.Count(l => l == CallQuality.Poor),
            streams.Max(s => s.RoundTrip?.TotalMilliseconds),
            streams.Max(s => s.PacketLossRate),
            streams.Max(s => s.Jitter?.TotalMilliseconds));
    }
}
