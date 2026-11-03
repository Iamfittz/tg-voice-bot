using VoiceBot.Domain;
using Xunit;

namespace VoiceBot.Tests;

public class MessageSplitterTests
{
    [Fact]
    public void ShortText_StaysInOnePart()
    {
        Assert.Equal(["hello"], MessageSplitter.Split("hello"));
    }

    [Fact]
    public void LongText_EveryPartFitsTelegramLimit_AndNoWordIsLost()
    {
        var words = Enumerable.Range(0, 3000).Select(i => "word" + i).ToArray();

        var parts = MessageSplitter.Split(string.Join(' ', words));

        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, MessageSplitter.TelegramLimit));
        Assert.Equal(words, string.Join(' ', parts).Split(' '));
    }

    [Fact]
    public void TextWithoutSpaces_IsCutAtLimit_WithoutLosingCharacters()
    {
        var text = new string('a', 10);

        var parts = MessageSplitter.Split(text, limit: 4);

        Assert.Equal(["aaaa", "aaaa", "aa"], parts);
    }

    [Fact]
    public void SurrogatePair_IsNotCutInHalf()
    {
        var parts = MessageSplitter.Split("aaa😀bbb", limit: 4);

        Assert.Equal("aaa", parts[0]);
        Assert.StartsWith("😀", parts[1]);
    }
}

public class VoiceAdmissionTests
{
    private static readonly LimitOptions Limits = new()
    {
        MaxDurationSeconds = 300,
        MaxFileBytes = 1000,
        MaxPendingPerUser = 3
    };

    [Theory]
    [InlineData(300, 1000L, 2, true, AdmissionResult.Accepted)]
    [InlineData(300, null, 0, true, AdmissionResult.Accepted)]
    [InlineData(301, 1000L, 0, true, AdmissionResult.TooLong)]
    [InlineData(10, 1001L, 0, true, AdmissionResult.TooBig)]
    [InlineData(10, 100L, 3, true, AdmissionResult.UserBusy)]
    [InlineData(10, 100L, 0, false, AdmissionResult.QueueFull)]
    public void Check_AppliesEveryLimit(int duration, long? bytes, int pending, bool hasSpace, AdmissionResult expected)
    {
        Assert.Equal(expected, VoiceAdmission.Check(duration, bytes, pending, hasSpace, Limits));
    }
}
