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

    // --- Δ C037: proxy booking (P-02, P-13) ---------------------------------------------------

    /// <summary>
    /// A booker asked a rider where they are (ADD §11.15). notification-svc branches on the
    /// payload's <c>state</c>: <c>Pending</c> is the FCM data message to a registered rider,
    /// <c>RiderNotRegistered</c> is AL-45's <c>pickup_confirm</c> token minted and SMSed instead.
    /// </summary>
    public const string LocationRequestIssued = "location.request.issued";

    /// <summary>
    /// The rider shared their position. fanout-svc publishes it to the booker's WebSocket group
    /// <c>booker:{bookerId}:loc-req:{requestId}</c> (P-13) and the booker's pickup pin follows.
    /// </summary>
    public const string LocationRequestConfirmed = "location.request.confirmed";

    /// <summary>
    /// The rider refused. <b>Carries no coordinates and never can</b> (P-02) — the payload type has
    /// no place to put one.
    /// </summary>
    public const string LocationRequestDeclined = "location.request.declined";

    /// <summary>The 300 s window closed unanswered; the booker falls back to a map pin (US-8.19).</summary>
    public const string LocationRequestExpired = "location.request.expired";

    // --- Δ C037: package delivery (P-07, P-08, AL-21) -----------------------------------------

    /// <summary>
    /// The sender's OTP was read out and the parcel changed hands (ADD §11.16). AL-21's branch
    /// hangs off this one: notification-svc pushes to a registered recipient and SMSes an
    /// unregistered one a <c>safety.trip_share_tokens</c> tracking link, and the delivery OTP
    /// travels with it.
    /// </summary>
    public const string PackagePickedUp = "package.picked_up";

    /// <summary>
    /// Handed over — by the recipient's OTP, or by photo proof when nobody was there (P-10).
    /// Co-fires with <see cref="Completed"/>, which is the state change consumers already read.
    /// </summary>
    public const string PackageDelivered = "package.delivered";

    /// <summary>
    /// Five wrong codes on one gate: the handoff now needs a human (P-07's "expired/exhausted →
    /// admin queue"). <b>No spec prints a name</b> — coined here and raised in the C037 handoff,
    /// because "admin queue" is a support-svc ticket and D6' §2.4 makes the outbox the only way one
    /// bounded context asks another for something.
    /// </summary>
    public const string PackageOtpLocked = "package.otp_locked";

    /// <summary>
    /// The driver confirmed the cash-on-delivery amount was collected (P-08: "settlement event
    /// <c>payment.cod_collected</c> posts driver earning identically to <c>CashSettled</c>").
    /// Carries the same payload as <see cref="Settled"/>, so a consumer reads one shape.
    /// </summary>
    public const string CashOnDeliveryCollected = "payment.cod_collected";
}
