using System.Text.Json;
using MageRide.Provisioning.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.Provisioning.Trackers;

/// <summary>The event names provisioning-svc publishes on <c>provisioning.events</c>.</summary>
public static class TrackerEventTypes
{
    /// <summary>D3' <c>POST /v1/trackers/bind</c> side effect; D6' §4.3 cache prime.</summary>
    public const string TrackerBound = "tracker.bound";

    /// <summary>D6' §4.3 cache invalidation. Emitted by an unbind, a decommission and a quarantine alike.</summary>
    public const string TrackerUnbound = "tracker.unbound";

    /// <summary>T-12. Names the credential serials that stopped being valid.</summary>
    public const string TrackerRevoked = "tracker.revoked";

    /// <summary>T-08, US-3.4. The admin alert: two devices claimed one IMEI and both are held.</summary>
    public const string TrackerQuarantined = "tracker.quarantined";

    /// <summary>T-02. A credential was replaced inside its renewal window; the old one still works.</summary>
    public const string CredentialRotated = "tracker.credential_rotated";

    /// <summary>US-3.6. Which of the two possible publishers is now authoritative for this vehicle.</summary>
    public const string SourceSwitched = "tracker.source_switched";
}

/// <summary>
/// The envelopes provisioning-svc writes into <c>prov.outbox</c> (D6' §2.4, migration 0403).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of the six are named by a spec and none of them has an envelope anywhere in D6' §2.2.</b>
/// D3' lists "emit <c>tracker.bound</c>" as a side effect of the bind and D6' §4.3 names
/// <c>tracker.bound</c>/<c>tracker.unbound</c> as the cache-invalidation pair; the shapes below
/// are provisioning-svc's and are raised as a micro-change-set in the C030 handoff.
/// </para>
/// <para>
/// <b>The aggregate id is always the vehicle</b>, matching the topic's partition key. Keying by
/// binding id would order events per binding, and the one ordering that matters is per vehicle: a
/// tracker moved from vehicle A to vehicle B produces an unbind and a bind that a consumer must
/// apply in that order or it rebuilds the cache entry it just dropped.
/// </para>
/// <para>
/// <b>No payload carries credential material.</b> A rotation names the outgoing and incoming
/// serials and nothing else — the secret half of a credential is returned to the caller that
/// minted it, once, over TLS, and putting it on a topic with a seven-day retention would undo
/// that.
/// </para>
/// </remarks>
public static class TrackerEvents
{
    public static OutboxRecord TrackerBound(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return Record(
            TrackerEventTypes.TrackerBound,
            binding.VehicleId,
            new
            {
                bindingId = binding.Id,
                imei = binding.Imei,
                vehicleId = binding.VehicleId,
                fleetId = binding.FleetId,
                credentialType = binding.CredentialType,
                credentialSerial = binding.CredentialSerial,
                rotatesAt = binding.RotatesAt,
                boundAt = binding.CreatedAt,
            });
    }

    /// <summary>
    /// The D6' §4.3 invalidation. <paramref name="reason"/> distinguishes the owner's unbind from
    /// an admin's decommission and from a quarantine — every consumer drops the cache entry either
    /// way, and a support engineer reading the topic does not.
    /// </summary>
    public static OutboxRecord TrackerUnbound(TrackerBinding binding, string reason)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return Record(
            TrackerEventTypes.TrackerUnbound,
            binding.VehicleId,
            new
            {
                bindingId = binding.Id,
                imei = binding.Imei,
                vehicleId = binding.VehicleId,
                reason,
                unboundAt = binding.StateChangedAt,
            });
    }

    /// <summary>T-12 — the durable twin of the Redis signal.</summary>
    public static OutboxRecord TrackerRevoked(TrackerBinding binding, IReadOnlyCollection<string> serials, string reason)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(serials);

        return Record(
            TrackerEventTypes.TrackerRevoked,
            binding.VehicleId,
            new
            {
                bindingId = binding.Id,
                imei = binding.Imei,
                vehicleId = binding.VehicleId,
                credentialSerials = serials,
                reason,
                revokedAt = binding.StateChangedAt,
            });
    }

    /// <summary>
    /// T-08's admin alert (US-3.4).
    /// </summary>
    /// <remarks>
    /// One event per incident, not one per held binding. The operator's question is "which of
    /// these two devices is the real one", and two events each naming one side is the shape that
    /// cannot answer it. <paramref name="competingSerials"/> carries the credentials seen claiming
    /// the IMEI — two bindings' worth when the clone showed up at <c>bind</c>, and the incumbent's
    /// plus whatever a stranger presented when it showed up at <c>validate</c>.
    /// </remarks>
    public static OutboxRecord TrackerQuarantined(
        TrackerBinding held,
        IReadOnlyCollection<TrackerBinding> heldBindings,
        IReadOnlyCollection<string> competingSerials,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(heldBindings);
        ArgumentNullException.ThrowIfNull(competingSerials);

        return Record(
            TrackerEventTypes.TrackerQuarantined,
            held.VehicleId,
            new
            {
                imei = held.Imei,
                detail,
                quarantinedAt = held.StateChangedAt,
                competingSerials,
                holders = heldBindings.Select(Holder).ToArray(),
            });
    }

    public static OutboxRecord CredentialRotated(
        TrackerBinding binding, string previousSerial, string newSerial, DateTimeOffset rotatedAt) =>
        Record(
            TrackerEventTypes.CredentialRotated,
            binding is null ? throw new ArgumentNullException(nameof(binding)) : binding.VehicleId,
            new
            {
                bindingId = binding.Id,
                imei = binding.Imei,
                vehicleId = binding.VehicleId,
                previousSerial,
                credentialSerial = newSerial,
                rotatesAt = binding.RotatesAt,
                rotatedAt,
            });

    public static OutboxRecord SourceSwitched(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return Record(
            TrackerEventTypes.SourceSwitched,
            binding.VehicleId,
            new
            {
                bindingId = binding.Id,
                imei = binding.Imei,
                vehicleId = binding.VehicleId,
                source = binding.Source,
                switchedAt = binding.StateChangedAt,
            });
    }

    private static object Holder(TrackerBinding binding) => new
    {
        bindingId = binding.Id,
        vehicleId = binding.VehicleId,
        credentialSerial = binding.CredentialSerial,
        boundAt = binding.CreatedAt,
        lastSeenAt = binding.LastSeenAt,
    };

    private static OutboxRecord Record(string eventType, Guid vehicleId, object payload) =>
        new(vehicleId, eventType, JsonSerializer.Serialize(payload, MageRideJson.StorageOptions));
}
