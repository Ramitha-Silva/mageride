namespace MageRide.Shared.Messaging;

/// <summary>One record on its way to Redpanda.</summary>
/// <param name="Topic">Topic from the D6' §2.1 registry.</param>
/// <param name="Key">Partition key — <c>vehicleId</c> for telemetry/trip topics, <c>rideId</c> for
/// <c>ride.events</c>/<c>dispatch.events</c>. Ordering is per partition, so the key is what makes
/// events for one aggregate arrive in order (D6' §2.3).</param>
/// <param name="Value">Serialised event body.</param>
/// <param name="Headers">Optional message headers, e.g. <c>eventType</c>, <c>eventId</c>.</param>
public sealed record EventMessage(
    string Topic,
    string Key,
    ReadOnlyMemory<byte> Value,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Where the broker put a record: the partition it chose and the offset it wrote at.</summary>
/// <param name="Topic">Topic the record landed on.</param>
/// <param name="Partition">Partition the key hashed to. Ordering is a per-partition guarantee.</param>
/// <param name="Offset">Offset the record was written at, or <c>-1</c> from a publisher that does
/// not talk to a broker.</param>
/// <remarks>
/// Returned so a producer on an at-least-once ingest path can say <i>where</i> a payload landed
/// before it acknowledges upstream. mqtt-bridge-svc is the caller that needs it: ADD §7.3 asks the
/// bridge to "commit Redpanda offsets per partition", and the only offset a producer has any say
/// over is the one the broker assigned to the record it just wrote (C038 handoff).
/// </remarks>
public readonly record struct PublishReceipt(string Topic, int Partition, long Offset)
{
    /// <summary>A receipt from a publisher with no broker behind it.</summary>
    public static PublishReceipt None(string topic) => new(topic, -1, -1);
}

/// <summary>
/// Publishes to the event backbone (D6' §2). The outbox dispatcher is the only caller on a write
/// path — services never publish directly, they write an outbox row (R-13).
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes and waits for the broker's acknowledgement.</summary>
    Task<PublishReceipt> PublishAsync(EventMessage message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a batch and waits for all acknowledgements.</summary>
    Task<IReadOnlyList<PublishReceipt>> PublishAsync(
        IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default);
}
