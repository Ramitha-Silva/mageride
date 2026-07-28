namespace MageRide.Ride.Domain;

/// <summary>
/// The <c>ride.events</c> types this service emits (D6' §2.1/§2.2, ADD §11.12's "Events emitted").
/// </summary>
/// <remarks>
/// <para>
/// D6' §2.2 lists <c>offer.created</c> on <c>ride.events</c> as well as on <c>dispatch.events</c>,
/// and ADD §11.11 writes it to <c>rides.outbox</c> — so the ride-side row is the one that commits
/// with the state change, which is the whole point of R-13. <c>offer.declined</c> is named by
/// §11.12's matrix without a topic; it rides here for the same reason, because ride-svc is what
/// performed the <c>Offered → Matching</c> move dispatch-svc needs to hear about.
/// </para>
/// <para>
/// <b>Names D6' §2.2 does not print.</b> Its <c>eventType</c> comment is a partial list ("…"), and
/// §11.12's matrix names six more in its Events column — every terminal below plus
/// <c>cancellation.penalty.accrued</c> and <c>reputation.driver_cancelled</c>. Those are used
/// verbatim. <see cref="Settled"/> is the one name **no** spec prints and is this service's; it is
/// raised in the C032 handoff.
/// </para>
/// <para>
/// Not emitted: anything for <c>Requested → Matching</c>. dispatch-svc drives that move itself, so
/// an event would only tell it what it just did, and the registry has no name for one.
/// </para>
/// </remarks>
public static class RideEventTypes
{
    public const string Requested = "ride.requested";
    public const string OfferCreated = "offer.created";
    public const string OfferDeclined = "offer.declined";

    /// <summary>
    /// The 15 s window closed with no answer (D5' §6's <c>Offered | Offer expires 15 s | →Matching
    /// | … | offer.expired</c> row, ADD §11.11's R-04 backstop). Emitted here for the same reason as
    /// <see cref="OfferDeclined"/>: ride-svc is what performed the <c>Offered → Matching</c> move.
    /// </summary>
    public const string OfferExpired = "offer.expired";
    public const string Accepted = "ride.accepted";
    public const string DriverArrived = "ride.driver_arrived";
    public const string Started = "ride.started";
    public const string Completed = "ride.completed";

    // --- The §11.12 matrix's Events column ----------------------------------------------------

    /// <summary>Every cancellation terminal, whoever caused it (§11.12 rows 1, 5, 6, 7, 12, 13).</summary>
    public const string Cancelled = "ride.cancelled";

    /// <summary>dispatch-svc ran out of candidates (US-6A.11).</summary>
    public const string ExpiredNoDriver = "ride.expired_no_driver";

    public const string NoShowRider = "ride.no_show_rider";
    public const string NoShowDriver = "ride.no_show_driver";

    /// <summary>Terminal-with-followup: the fraud-review queue picks it up (§11.12).</summary>
    public const string Disputed = "ride.disputed";

    /// <summary>
    /// The Rs 50 / Rs 100 / full-fare accrual (D-05, D5' §7.1). fare-svc settles it against the
    /// passenger's next completed trip; ride-svc only states that it is owed.
    /// </summary>
    public const string PenaltyAccrued = "cancellation.penalty.accrued";

    /// <summary>The counter reputation-svc (C033) increments for a driver-side cancel (§11.12).</summary>
    public const string DriverCancelled = "reputation.driver_cancelled";

    /// <summary>
    /// The ride reached its terminal money state (R-05). <b>Not in D6' §2.2</b> — see the class
    /// remarks. The driver's earning posts from here and never before.
    /// </summary>
    public const string Settled = "ride.settled";
}
