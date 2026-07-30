namespace MageRide.Shared.Messaging;

/// <summary>
/// The D6' §2.1 topic registry, spelled once.
/// </summary>
/// <remarks>
/// <para>
/// A producer and a consumer that disagree about a topic name do not fail — Redpanda's
/// <c>auto_create_topics_enabled</c> is on in the dev stack, so the typo becomes a real,
/// empty, one-partition topic and the pipeline simply goes quiet. That is the failure these
/// constants exist to prevent; <c>infra/deploy/redpanda/bootstrap-topics.sh</c> creates the same
/// six with their spec'd partition counts.
/// </para>
/// <para>
/// <b>Partition keys are part of the contract.</b> The telemetry and trip topics are keyed by
/// <c>vehicleId</c> and the ride/dispatch topics by <c>rideId</c>, which is what makes ordering
/// per aggregate hold end to end (D6' §2.3).
/// </para>
/// </remarks>
public static class EventTopics
{
    /// <summary>Device payloads verbatim, as mqtt-bridge-svc lifted them off EMQX. Key: vehicleId.</summary>
    public const string TelemetryRaw = "telemetry.raw";

    /// <summary>Canonical <see cref="Telemetry.PositionSample"/>s. Key: vehicleId.</summary>
    public const string TelemetryNormalized = "telemetry.normalized";

    /// <summary>Mode A/B session transitions from trip-state-svc. Key: vehicleId.</summary>
    public const string TripEvents = "trip.events";

    /// <summary>ride-svc's outbox. Key: rideId.</summary>
    public const string RideEvents = "ride.events";

    /// <summary>dispatch-svc's outbox — <c>offer.created</c> and friends. Key: rideId.</summary>
    public const string DispatchEvents = "dispatch.events";

    /// <summary>
    /// registry-svc's outbox — <c>share.revoked</c> (D-22) and friends. Key: vehicleId.
    /// </summary>
    /// <remarks>
    /// <b>Not in D6' §2.1's six-topic registry</b> — a micro-change-set raised in the C028
    /// handoff. D3' gives <c>share.revoked</c> a producer (registry-svc) and D6' §5.2 a consumer
    /// (fanout-svc, which turns it into a directed <c>RemoveFromGroupAsync</c> inside 200 ms) and
    /// neither gives it a topic. Publishing it on <c>trip.events</c> instead would put a registry
    /// event on trip-state-svc's stream, where its consumers are the wrong set.
    /// </remarks>
    public const string RegistryEvents = "registry.events";

    /// <summary>
    /// provisioning-svc's outbox — <c>tracker.bound</c>, <c>tracker.unbound</c>,
    /// <c>tracker.revoked</c>, <c>tracker.quarantined</c>. Key: vehicleId.
    /// </summary>
    /// <remarks>
    /// <b>Not in D6' §2.1's six-topic registry</b> either — a micro-change-set raised in the C030
    /// handoff, the same shape as <see cref="RegistryEvents"/>. D3' `POST /v1/trackers/bind` lists
    /// "emit <c>tracker.bound</c>" as a side effect and D6' §4.3 has the IMEI cache "invalidated by
    /// <c>tracker.bound</c>/<c>tracker.unbound</c>", so both have a producer and a consumer and
    /// neither has a topic. Keyed by vehicleId rather than by IMEI or binding id: a re-bind of the
    /// same IMEI to a new vehicle emits an unbind and a bind, and only the vehicle key keeps a
    /// consumer's cache rebuild in the order the two happened.
    /// </remarks>
    public const string ProvisioningEvents = "provisioning.events";

    /// <summary>
    /// reputation-svc's outbox — <c>fraud.suspected</c> (E-07) and
    /// <c>reputation.block_state_changed</c> (D-04). Key: userId.
    /// </summary>
    /// <remarks>
    /// <b>Not in D6' §2.1's six-topic registry</b> — a micro-change-set raised in the C033 handoff,
    /// the same shape as <see cref="RegistryEvents"/> and <see cref="ProvisioningEvents"/>. ADD §6
    /// gives <c>fraud.suspected</c> a producer (reputation-svc "emits <c>fraud.suspected</c> for
    /// admin review") and §12.6 a consumer (the admin fraud queue), and neither gives it a topic.
    /// Keyed by userId rather than by flag or ride id: a block state is a fact about a person, and
    /// only the user key keeps two consequences for one person in the order they happened.
    /// </remarks>
    public const string ReputationEvents = "reputation.events";

    /// <summary>
    /// fleet-health-svc's outbox — <c>fleet.health_alert</c> (US-3.16). Key: fleetId.
    /// </summary>
    /// <remarks>
    /// <b>Not in D6' §2.1's six-topic registry</b> — a micro-change-set raised in the C044
    /// handoff, the same shape as <see cref="RegistryEvents"/>, <see cref="ProvisioningEvents"/>
    /// and <see cref="ReputationEvents"/>. C044's deliverable is "threshold alerts … emitted for
    /// the Fleet Portal and notification-svc", and US-3.16 gives the alert a consumer
    /// (notification-svc, by email/SMS) — so it has a named producer and a named consumer and no
    /// topic. Keyed by fleetId rather than by alert id: an alert is a fact about an organisation,
    /// and two windows' verdicts for one fleet have to arrive in the order they were reached or a
    /// recovery notice can precede the outage it clears.
    /// </remarks>
    public const string FleetEvents = "fleet.events";

    /// <summary>
    /// wallet-svc's outbox — <c>wallet.debited</c>, <c>wallet.credited</c>,
    /// <c>wallet.low_balance</c>. Key: the wallet owner (driverId or fleetId).
    /// </summary>
    /// <remarks>
    /// <b>Not in D6' §2.1's six-topic registry</b> — a micro-change-set raised in the C046 handoff,
    /// the same shape as the four above, and the best-named of them: ADD §6's wallet-svc row and the
    /// replica's both say "publishes <c>wallet.debited</c> / <c>wallet.credited</c> events that
    /// invalidate dispatch-svc's Redis balance cache", D5' §9.2 makes the cache "debit-invalidated
    /// (<c>wallet.debited</c> event clears)", and ride-svc's ADD row lists <c>wallet.debited</c>
    /// among what it consumes. Two named producers' worth of consumers, and no topic.
    /// <para>
    /// Keyed by the wallet <i>owner</i> rather than by the journal entry: a debit and the credit that
    /// follows it must reach the D-08 cache in the order they happened, and only the owner key
    /// guarantees that. <c>wallet.low_balance</c> rides the same topic because it is a fact about the
    /// same wallet and its consumer (notification-svc, US-9.9) is reading it anyway.
    /// </para>
    /// </remarks>
    public const string WalletEvents = "wallet.events";

    /// <summary>Audit trail (D-35). Key: entityId.</summary>
    public const string AuditEvents = "audit.events";

    /// <summary>The registry, in the order <c>bootstrap-topics.sh</c> creates them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TelemetryRaw, TelemetryNormalized, TripEvents, RideEvents, DispatchEvents, RegistryEvents,
        ProvisioningEvents, ReputationEvents, FleetEvents, WalletEvents, AuditEvents,
    ];

    /// <summary>
    /// The dead-letter topic for <paramref name="topic"/> — <c>&lt;topic&gt;.dlq</c>, carrying
    /// <c>{originalOffset, error, attempts}</c> (D6' §2.3).
    /// </summary>
    /// <remarks>
    /// Named here so the convention is fixed before anything writes one. No component owns the DLQ
    /// yet: C024's consumers stall a partition on a retryable failure and commit past a poison
    /// message, which is loud rather than lossy, and C034/C039 land the durable form.
    /// </remarks>
    public static string DeadLetter(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return $"{topic}.dlq";
    }
}
