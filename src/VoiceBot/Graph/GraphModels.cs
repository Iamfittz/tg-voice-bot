using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace VoiceBot.Graph;

public sealed record GraphPage<T>(
    List<T>? Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

public sealed record GraphSubscription(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    string? Resource,
    string? ChangeType,
    string? NotificationUrl,
    string? LifecycleNotificationUrl,
    DateTimeOffset? ExpirationDateTime,
    string? ClientState);

public sealed record ChangeNotificationBatch(List<ChangeNotification>? Value);

public sealed record ChangeNotification(
    string? SubscriptionId,
    string? ClientState,
    string? TenantId,
    string? ChangeType,
    string? Resource,
    string? LifecycleEvent,
    ChangeNotificationResource? ResourceData);

public sealed record ChangeNotificationResource(string? Id);

public sealed record GraphCallRecord(
    string Id,
    long Version,
    string? Type,
    List<string>? Modalities,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime)
{
    public List<GraphSession>? Sessions { get; init; }

    [JsonPropertyName("sessions@odata.nextLink")]
    public string? SessionsNextLink { get; init; }

    [JsonPropertyName("participants_v2")]
    public List<GraphParticipant>? Participants { get; init; }

    [JsonPropertyName("participants_v2@odata.nextLink")]
    public string? ParticipantsNextLink { get; init; }
}

public sealed record GraphParticipant(string? Id, GraphIdentitySet? Identity)
{
    public string Name =>
        Identity?.User?.DisplayName
        ?? Identity?.Guest?.DisplayName
        ?? Identity?.Application?.DisplayName
        ?? Identity?.Phone?.DisplayName
        ?? Identity?.Phone?.Id
        ?? "Unknown";
}

public sealed record GraphIdentitySet(GraphIdentity? User, GraphIdentity? Guest, GraphIdentity? Application, GraphIdentity? Phone);

public sealed record GraphIdentity(string? Id, string? DisplayName);

public sealed record GraphSession(List<GraphSegment>? Segments);

public sealed record GraphSegment(List<GraphMedia>? Media);

public sealed record GraphMedia(string? Label, List<GraphStream>? Streams);

public sealed record GraphStream(
    string? StreamDirection,
    long? PacketUtilization,
    double? AveragePacketLossRate,
    [property: JsonConverter(typeof(IsoDurationConverter))] TimeSpan? AverageRoundTripTime,
    [property: JsonConverter(typeof(IsoDurationConverter))] TimeSpan? AverageJitter);

// Graph sends durations in ISO 8601, for example PT0.016S.
public sealed class IsoDurationConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { Length: > 0 } value ? XmlConvert.ToTimeSpan(value) : null;

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(XmlConvert.ToString(value.Value));
    }
}
