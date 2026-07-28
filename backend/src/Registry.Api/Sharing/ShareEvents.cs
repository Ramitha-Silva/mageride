using System.Text.Json;
using MageRide.Registry.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Registry.Sharing;

/// <summary>
/// The envelopes registry-svc writes into <c>registry.outbox</c> (D6' §2.4, migration 0309).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every payload names the passenger, and D6' §5.1 does not.</b> The hub-event table gives
/// <c>ShareRevoked</c> a payload of <c>{vehicleId}</c> — which fanout-svc cannot act on, because
/// §5.2 in the same document requires a <b>directed</b> <c>RemoveFromGroupAsync</c> "to affected
/// passenger &lt; 200 ms". A vehicle id alone leaves fanout two options, both wrong: remove
/// everybody watching the vehicle, or query registry-svc on the hot path to find out who. This
/// component's DoD says the event "carries the passenger id fanout needs", so it does. Recorded
/// as a micro-change-set against D6' §5.1 in the C028 handoff.
/// </para>
/// <para>
/// The aggregate id is the <b>vehicle</b>, not the grant: it is the Kafka partition key, and
/// keying by grant would let a later <c>share.granted</c> for the same passenger overtake the
/// <c>share.revoked</c> that preceded it and restore visibility that was taken away.
/// </para>
/// </remarks>
public static class ShareEvents
{
    /// <summary>D-22. The grantee loses live visibility of the vehicle.</summary>
    /// <param name="reason">
    /// Why. <c>revoked</c> for the owner's explicit <c>DELETE</c>, <c>vehicle-deactivated</c> for
    /// the cascade US-2.16 triggers. fanout does the same thing either way; a support engineer
    /// reading the topic does not.
    /// </param>
    public static OutboxRecord ShareRevoked(ShareGrant grant, string reason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return Record(
            RegistryEventTypes.ShareRevoked,
            grant.VehicleId,
            new
            {
                grantId = grant.Id,
                vehicleId = grant.VehicleId,
                // The field D6' §5.1 omits and §5.2 needs.
                passengerId = grant.GranteeUserId,
                reason,
                revokedAt = grant.RevokedAt,
            });
    }

    /// <summary>The counterpart, so a consumer's <c>share:{userId}</c> cache can be warmed as well as invalidated (D-23).</summary>
    public static OutboxRecord ShareGranted(ShareGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return Record(
            RegistryEventTypes.ShareGranted,
            grant.VehicleId,
            new
            {
                grantId = grant.Id,
                vehicleId = grant.VehicleId,
                passengerId = grant.GranteeUserId,
                acceptedAt = grant.AcceptedAt,
                expiresAt = grant.ExpiresAt,
            });
    }

    /// <summary>US-2.16. The vehicle is off the map; every grant on it was revoked with it.</summary>
    public static OutboxRecord VehicleDeactivated(Guid vehicleId, Guid ownerId) =>
        Record(
            RegistryEventTypes.VehicleDeactivated,
            vehicleId,
            new { vehicleId, ownerId });

    private static OutboxRecord Record(string eventType, Guid vehicleId, object payload) =>
        new(vehicleId, eventType, JsonSerializer.Serialize(payload, MageRideJson.StorageOptions));
}
