namespace MageRide.Shared.Messaging;

/// <summary>
/// A domain event queued for publication, written in the same transaction as the state change it
/// describes (D6' §2.4, R-13). Matches the <c>rides.outbox</c> columns (D4' §5).
/// </summary>
/// <param name="AggregateId">Aggregate the event is about. Becomes the Kafka partition key, which
/// is what gives in-order delivery per ride/vehicle (D6' §2.1/§2.3).</param>
/// <param name="EventType">Event name from the D6' §2.2 registry, e.g. <c>ride.accepted</c>.</param>
/// <param name="Payload">The full event envelope as JSON. The kernel does not shape it — the
/// owning service does, to its topic's schema.</param>
public sealed record OutboxRecord(Guid AggregateId, string EventType, string Payload)
{
    public static OutboxRecord Create(Guid aggregateId, string eventType, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        if (aggregateId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox row needs a real aggregate id; it is the Kafka partition key (D6' §2.1).", nameof(aggregateId));
        }

        return new OutboxRecord(aggregateId, eventType, payloadJson);
    }
}

/// <summary>An outbox row read back for dispatch.</summary>
/// <param name="Id">Monotonic identity column; dispatch order.</param>
public sealed record OutboxRow(long Id, Guid AggregateId, string EventType, string Payload, DateTimeOffset CreatedAt);
