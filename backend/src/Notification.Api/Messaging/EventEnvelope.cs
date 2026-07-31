using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MageRide.Shared.Messaging;

namespace MageRide.Notification.Messaging;

/// <summary>
/// One message off a topic, decoded far enough to route it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The event type comes from the header first.</b> <c>OutboxDispatcher</c> puts <c>eventType</c>
/// on every message it publishes, and it is the only place some producers put it: registry-svc and
/// wallet-svc serialise their <em>payload</em> into the outbox row, with no <c>eventType</c> member
/// inside the JSON, while ride-svc wraps its payload in an envelope that has one. Reading the header
/// is what lets one consumer shape serve all four topics without each producer's envelope having to
/// agree.
/// </para>
/// <para>
/// <b>The body is exposed as a <see cref="JsonElement"/> rather than a typed record.</b> This
/// service reads a handful of members out of nine event shapes owned by four other bounded
/// contexts, and a typed mirror of each would be nine copies that go stale silently when a producer
/// adds a field. What it actually needs is "give me <c>bookerId</c> if there is one" — so that is
/// the interface, and a missing member is a routing decision rather than a deserialisation failure.
/// </para>
/// </remarks>
public sealed class EventEnvelope
{
    private readonly JsonDocument _document;

    private EventEnvelope(string eventType, string key, JsonDocument document)
    {
        EventType = eventType;
        Key = key;
        _document = document;
        Root = document.RootElement;

        // ride-svc nests the domain fields under `payload`; the other three put them at the top
        // level. Resolving it once here is what keeps every handler's reads flat.
        Body = Root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
            ? payload
            : Root;
    }

    /// <summary>The D6' §2.2 event name.</summary>
    public string EventType { get; }

    /// <summary>The message key — the aggregate id, and the partition key (D6' §2.1).</summary>
    public string Key { get; }

    /// <summary>The whole envelope.</summary>
    public JsonElement Root { get; }

    /// <summary>The domain fields, whether or not the producer nested them.</summary>
    public JsonElement Body { get; }

    /// <summary>
    /// The event's own identity, when it carries one. Used in the dedupe key so a redelivery
    /// collides — and falling back to the message key is deliberate: a producer that publishes no
    /// event id still dedupes per aggregate, which for the four types that lack one is exactly
    /// right.
    /// </summary>
    public string Identity => Guid(Root, "eventId")?.ToString() ?? Key;

    public static EventEnvelope? TryParse(ConsumeResult<string, byte[]> message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var json = message.Message.Value is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        var eventType = HeaderValue(message.Message.Headers, "eventType")
                        ?? String(document.RootElement, "eventType");

        if (string.IsNullOrWhiteSpace(eventType))
        {
            document.Dispose();
            return null;
        }

        return new EventEnvelope(eventType, message.Message.Key ?? string.Empty, document);
    }

    /// <summary>A string member of the body, or <see langword="null"/>.</summary>
    public string? Text(string name) => String(Body, name);

    /// <summary>A GUID member of the body, or <see langword="null"/> when absent or malformed.</summary>
    public Guid? Id(string name) => Guid(Body, name);

    /// <summary>A number member of the body, or <see langword="null"/>.</summary>
    public long? Number(string name) =>
        Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    public bool Flag(string name) =>
        Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public DateTimeOffset? Instant(string name) =>
        Body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;

    private static string? HeaderValue(Headers? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        return headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? Guid(JsonElement element, string name) =>
        String(element, name) is { } text && System.Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Frees the parsed document. Consumers dispose after handling one message.</summary>
    public void Dispose() => _document.Dispose();
}

/// <summary>The four topics this service reads, and the group it reads them as.</summary>
public static class NotificationTopics
{
    public static readonly IReadOnlyList<string> All =
    [
        EventTopics.DispatchEvents,
        EventTopics.RideEvents,
        EventTopics.WalletEvents,
        EventTopics.RegistryEvents,
    ];
}
