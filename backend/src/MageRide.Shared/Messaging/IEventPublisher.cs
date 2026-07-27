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

/// <summary>
/// Publishes to the event backbone (D6' §2). The outbox dispatcher is the only caller on a write
/// path — services never publish directly, they write an outbox row (R-13).
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes and waits for the broker's acknowledgement.</summary>
    Task PublishAsync(EventMessage message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a batch and waits for all acknowledgements.</summary>
    Task PublishAsync(IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default);
}
