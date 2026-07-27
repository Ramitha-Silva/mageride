package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.ride.RideKind

private val ALL_KINDS: Set<RideKind> = RideKind.entries.toSet()
private val PASSENGER_KINDS: Set<RideKind> = setOf(RideKind.PASSENGER, RideKind.PROXY)
private val PACKAGE_ONLY: Set<RideKind> = setOf(RideKind.PACKAGE)

/**
 * Every ride transition an **app** can ask for, and what it needs to be legal.
 *
 * Not the same list as [RideTrigger]: a trigger is an edge of the state machine, a command is a
 * button. Several triggers have no command at all ([RideTrigger.DISPATCH_STARTED],
 * [RideTrigger.OFFER_EXPIRED]) because no app can cause them, and two commands share one trigger —
 * [START] and [VERIFY_PICKUP_OTP] both fire [RideTrigger.RIDE_STARTED], differing only in which
 * `kind` they belong to.
 *
 * The `kinds` gate is the P-06/P-07 fence in one place. A `package` ride has no rider to read out
 * a start OTP, so it starts through `POST /v1/rides/{rideId}/package/pickup-otp`; a `passenger`
 * ride has no delivery OTP to verify. Sending the wrong one is a `400` the client can see coming.
 *
 * @property actor Who may send it.
 * @property trigger Which edge of ADD Appendix B.2 it fires.
 * @property kinds The booking kinds it applies to (ADD Appendix B.2 invariant 6).
 * @property operationId The `RideApi` call it becomes, so a screen and a client cannot drift.
 */
public enum class RideCommand(
    public val actor: RideActor,
    public val trigger: RideTrigger,
    public val kinds: Set<RideKind>,
    public val operationId: String,
) {

    /** Take the live offer. The atomic single-winner accept (R-02). */
    ACCEPT_OFFER(RideActor.DRIVER, RideTrigger.OFFER_ACCEPTED, ALL_KINDS, "acceptRideOffer"),

    /** Pass on the live offer. No penalty; dispatch cascades to the next candidate. */
    DECLINE_OFFER(RideActor.DRIVER, RideTrigger.OFFER_DECLINED, ALL_KINDS, "declineRideOffer"),

    /** The driver is at the pickup point. */
    MARK_ARRIVED(RideActor.DRIVER, RideTrigger.DRIVER_ARRIVED, ALL_KINDS, "markDriverArrived"),

    /** Begin a passenger or proxy ride, quoting the rider's start OTP. */
    START(RideActor.DRIVER, RideTrigger.RIDE_STARTED, PASSENGER_KINDS, "startRide"),

    /** Begin a package ride by verifying the sender's pickup OTP (P-07). */
    VERIFY_PICKUP_OTP(RideActor.DRIVER, RideTrigger.RIDE_STARTED, PACKAGE_ONLY, "verifyPackagePickupOtp"),

    /**
     * End the ride.
     *
     * On a package this is AL-33's "Delivery completed" and needs the delivery handoff satisfied
     * first — the recipient's OTP or a proof photo (P-10). [RideProjection] enforces that.
     */
    COMPLETE(RideActor.DRIVER, RideTrigger.RIDE_COMPLETED, ALL_KINDS, "completeRide"),

    /** Verify the recipient's delivery OTP (P-07, AL-21). */
    VERIFY_DELIVERY_OTP(RideActor.DRIVER, RideTrigger.RIDE_COMPLETED, PACKAGE_ONLY, "verifyPackageDeliveryOtp"),

    /** Cancel. What it costs depends on where the ride is — see [CancellationMatrix]. */
    CANCEL(RideActor.RIDER, RideTrigger.RIDER_CANCELLED, ALL_KINDS, "cancelRide"),

    /** The driver gives the ride back after accepting it. Reputation hit (D5' §7). */
    DRIVER_CANCEL(RideActor.DRIVER, RideTrigger.DRIVER_CANCELLED, ALL_KINDS, "cancelRide"),

    /** The driver confirms cash on delivery (P-08). Package rides booked `cod` only. */
    CONFIRM_COD(RideActor.DRIVER, RideTrigger.COD_COLLECTED, PACKAGE_ONLY, "confirmCashOnDelivery"),

    /** Open a dispute against a settled ride (E-05). Moves no money. */
    DISPUTE(RideActor.RIDER, RideTrigger.DISPUTE_RAISED, ALL_KINDS, "disputeRide"),
    ;

    /** Whether this command is about the live offer rather than about a ride in hand. */
    public val needsLiveOffer: Boolean get() = this == ACCEPT_OFFER || this == DECLINE_OFFER

    /** Whether [kind] may send it (ADD Appendix B.2 invariant 6, P-06/P-07). */
    public fun appliesTo(kind: RideKind): Boolean = kind in kinds
}

/** Why a command may not be sent from where the ride is. */
public enum class RideCommandRejection {

    /** The ride is over. Nothing moves a terminal aggregate (`409 ride-terminal`). */
    RIDE_TERMINAL,

    /** ADD Appendix B.2 draws no such edge from this state (`409 illegal-transition`). */
    ILLEGAL_TRANSITION,

    /** This command belongs to a different booking kind (P-06/P-07). */
    WRONG_KIND,

    /** There is no live offer to accept or decline. */
    NO_LIVE_OFFER,

    /** The 15-second offer TTL has already elapsed on this device (D5' §3.5). */
    OFFER_EXPIRED,

    /** A package cannot complete until the delivery OTP or a proof photo lands (P-07/P-10). */
    PACKAGE_HANDOFF_INCOMPLETE,

    /** Five OTP attempts are spent; this gate needs the admin queue (P-07). */
    OTP_LOCKED,
}

/**
 * Whether a command is worth sending.
 *
 * A [Rejected] verdict is a **local** answer that saves a round trip and lets a screen grey a
 * button out. It is never a claim about what the server would have done: the ride may have moved
 * on since the last snapshot, which is exactly why every mutation carries a `version` and can
 * still come back `409` (R-14).
 */
public sealed interface RideCommandVerdict {

    /** Send it. */
    public data object Allowed : RideCommandVerdict

    /**
     * Do not send it.
     *
     * @property reason Why, so the screen can say something better than "not now".
     */
    public data class Rejected(val reason: RideCommandRejection) : RideCommandVerdict

    /** Whether this verdict permits the call. */
    public val isAllowed: Boolean get() = this is Allowed
}
