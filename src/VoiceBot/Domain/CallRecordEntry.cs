namespace VoiceBot.Domain;

public sealed class CallRecordEntry
{
    public string Id { get; set; } = "";
    public long Version { get; set; }
    public string Type { get; set; } = "";
    public string Modalities { get; set; } = "";
    public string Participants { get; set; } = "";
    public int ParticipantCount { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public int DurationSeconds { get; set; }
    public CallQuality Quality { get; set; }
    public int AudioStreams { get; set; }
    public int PoorStreams { get; set; }
    public double? MaxRoundTripMs { get; set; }
    public double? MaxPacketLossRate { get; set; }
    public double? MaxJitterMs { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

// A call record to fetch from Graph. It is stored before the webhook answers,
// because Graph does not resend a notification that was already acknowledged.
public sealed class PendingCallRecord
{
    public string Id { get; set; } = "";

    // Grows with every notification, so a notification that arrives during processing is not lost.
    public long Revision { get; set; }
    public int Attempts { get; set; }
    public bool GaveUp { get; set; }
    public string? LastError { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
}
