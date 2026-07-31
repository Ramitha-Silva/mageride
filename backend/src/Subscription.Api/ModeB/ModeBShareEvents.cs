using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Subscriptions.Persistence;

namespace MageRide.Subscriptions.ModeB;

/// <summary>
/// The two <c>registry.events</c> types Mode B visibility is built from (D-22, D-23).
/// </summary>
/// <remarks>
/// <b>A third copy of two string constants, and deliberately so.</b> registry-svc declares them
/// (C028) and fanout-svc declares them again (C041) rather than referencing the producer, because a
/// project reference between two services is a coupling neither wants. This service is a second
/// producer of the same two events; it copies the names for the same reason and is covered by the
/// same contract test (C118).
/// </remarks>
public static class ModeBShareEventTypes
{
    /// <summary>US-4.3b — the grant is live, so visibility begins.</summary>
    public const string ShareGranted = "share.granted";

    /// <summary>D-22 — the passenger unsubscribed, so visibility ends.</summary>
    public const string ShareRevoked = "share.revoked";
}

/// <summary>
/// The envelopes this service writes into <c>subscription.outbox</c> (migration 1204), published to
/// <c>registry.events</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Byte-for-byte the shapes registry-svc publishes</b> (<c>Registry.Api/Sharing/ShareEvents.cs</c>),
/// because they reach the same consumer. fanout-svc reads <c>vehicleId</c> and <c>passengerId</c> off
/// the payload and the type off the Kafka <c>eventType</c> header, and skips — permanently, without
/// stalling the partition — any share event that names no passenger. An envelope of our own invention
/// would be dropped exactly that way: silently, and only for the passengers this component exists to
/// revoke.
/// </para>
/// <para>
/// <b>The aggregate id is the vehicle, not the grant.</b> It is the Kafka partition key. Keying by the
/// grant would let an accept's <c>share.granted</c> overtake the <c>share.revoked</c> of an earlier
/// unsubscribe on another partition and restore visibility that had been taken away — and because a
/// rejoin reuses the same grant row, keying by grant would not even separate the two.
/// </para>
/// <para>
/// <b>Why this service publishes at all, rather than calling registry-svc.</b> The unsubscribe is one
/// transaction over three tables — the grant, the subscription and the event — and D-22 gives it a
/// 200 ms budget to reach the passenger's socket. A cross-service call would put the revocation
/// outside the transaction that decides it, which is exactly the failure the outbox exists to prevent
/// (R-13): an unsubscribe that commits and then fails to publish leaves the passenger watching a
/// vehicle they have left.
/// </para>
/// </remarks>
public static class ModeBShareEvents
{
    /// <summary>The accept: the passenger may see this vehicle from now on.</summary>
    public static OutboxRecord ShareGranted(GrantRow grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return Record(
            ModeBShareEventTypes.ShareGranted,
            grant.VehicleId,
            new
            {
                grantId = grant.GrantId,
                vehicleId = grant.VehicleId,
                passengerId = grant.PassengerId,
                acceptedAt = grant.GrantedAt,
                expiresAt = (DateTimeOffset?)null,
            });
    }

    /// <summary>
    /// The unsubscribe (BR-23.11): the passenger loses live visibility of the vehicle.
    /// </summary>
    /// <param name="reason">
    /// Why. <c>unsubscribed</c> is the only value this service produces — registry-svc's
    /// <c>revoked</c> and <c>vehicle-deactivated</c> are its own. fanout does the same thing
    /// whichever it is; a support engineer reading the topic does not.
    /// </param>
    public static OutboxRecord ShareRevoked(GrantRow grant, DateTimeOffset revokedAt, string reason = "unsubscribed")
    {
        ArgumentNullException.ThrowIfNull(grant);

        return Record(
            ModeBShareEventTypes.ShareRevoked,
            grant.VehicleId,
            new
            {
                grantId = grant.GrantId,
                vehicleId = grant.VehicleId,
                // The field D6' §5.1's payload omits and §5.2 needs — without it fanout-svc must
                // either remove everybody watching the vehicle or query for who, on the hot path.
                passengerId = grant.PassengerId,
                reason,
                revokedAt,
            });
    }

    private static OutboxRecord Record(string eventType, Guid vehicleId, object payload) =>
        new(vehicleId, eventType, JsonSerializer.Serialize(payload, MageRideJson.StorageOptions));
}
