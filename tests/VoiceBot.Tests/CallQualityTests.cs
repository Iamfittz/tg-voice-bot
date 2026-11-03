using VoiceBot.Domain;
using Xunit;

namespace VoiceBot.Tests;

public sealed class CallQualityTests
{
    private static readonly TimeSpan GoodRoundTrip = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan GoodJitter = TimeSpan.FromMilliseconds(8);

    [Fact]
    public void AllMetricsInsideLimits_IsGood()
    {
        Assert.Equal(CallQuality.Good, CallQualityClassifier.Classify(new(9600, GoodRoundTrip, 0.001, GoodJitter)));
    }

    [Theory]
    [InlineData(500, 0.1, 30, CallQuality.Good)]
    [InlineData(501, 0.1, 30, CallQuality.Poor)]
    [InlineData(500, 0.11, 30, CallQuality.Poor)]
    [InlineData(500, 0.1, 31, CallQuality.Poor)]
    public void ThresholdsAreExclusive(double roundTripMs, double loss, double jitterMs, CallQuality expected)
    {
        var stream = new AudioStreamMetrics(9600, TimeSpan.FromMilliseconds(roundTripMs), loss, TimeSpan.FromMilliseconds(jitterMs));

        Assert.Equal(expected, CallQualityClassifier.Classify(stream));
    }

    [Theory]
    [InlineData(500L)]
    [InlineData(null)]
    public void TooFewPackets_IsUnclassified_EvenWithBadMetrics(long? packets)
    {
        var stream = new AudioStreamMetrics(packets, TimeSpan.FromSeconds(2), 0.5, TimeSpan.FromSeconds(1));

        Assert.Equal(CallQuality.Unclassified, CallQualityClassifier.Classify(stream));
    }

    [Fact]
    public void MissingMetric_IsUnclassified()
    {
        Assert.Equal(CallQuality.Unclassified, CallQualityClassifier.Classify(new(9600, GoodRoundTrip, null, GoodJitter)));
    }

    [Fact]
    public void MissingMetric_DoesNotHideAKnownProblem()
    {
        Assert.Equal(CallQuality.Poor, CallQualityClassifier.Classify(new(9600, null, 0.5, null)));
    }

    [Fact]
    public void OnePoorStream_MakesTheCallPoor_AndWorstValuesAreKept()
    {
        var summary = CallQualityClassifier.Summarize(
        [
            new(9600, GoodRoundTrip, 0.001, GoodJitter),
            new(9600, TimeSpan.FromMilliseconds(640), 0.12, TimeSpan.FromMilliseconds(45))
        ]);

        Assert.Equal(CallQuality.Poor, summary.Quality);
        Assert.Equal(2, summary.AudioStreams);
        Assert.Equal(1, summary.PoorStreams);
        Assert.Equal(640, summary.MaxRoundTripMs);
        Assert.Equal(0.12, summary.MaxPacketLossRate);
        Assert.Equal(45, summary.MaxJitterMs);
    }

    [Fact]
    public void NoStreams_IsUnclassified()
    {
        var summary = CallQualityClassifier.Summarize([]);

        Assert.Equal(CallQuality.Unclassified, summary.Quality);
        Assert.Null(summary.MaxRoundTripMs);
    }
}
