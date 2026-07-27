package lk.mageride.shared.domain.fare

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes

// What the client believes about one ride's payment.
//
// THE CLIENT NEVER ADVANCES A PAYMENT. Every move comes from a server-confirmed `PaymentStatus` —
// a REST response, a poll or a push — through [PaymentProjection.onServerState]. There is
// deliberately no `apply(trigger)`: fare-svc is the sole writer, and a client that walked its own
// machine would eventually show a passenger a payment that had already been refunded.
//
// A SERVER MOVE THE TABLE DOES NOT DRAW IS APPLIED AND FLAGGED, NEVER DROPPED. Same rule as
// [lk.mageride.shared.domain.ride.RideProjection] (C015), for the same reason: refusing the
// server's answer would leave a passenger looking at a settled ride that says it still owes money.
//
// NOTE ON ORDERING: `PaymentStatus` carries no version. `fares.ride_payments` has no optimistic
// concurrency column — the payment machine is driven by gateway callbacks that dedupe on
// `provider_transaction_id` (R-19) rather than by client mutations that need R-14. So this
// projection cannot drop a stale frame the way RideProjection can; it drops only an exact
// duplicate, and a terminal state is never walked back. That last rule is the one that matters in
// practice: an in-flight poll landing after the settling push must not un-settle the ride.

/**
 * The +5-minute nudge and the escalation behind it (AL-47, BR-30.1).
 *
 * "Claim without confirm: nudge push at +5 min; unresolved at ride-history view → 'Get help' →
 * Support ticket → Finance dispute queue (no wallet movement; evidence = claim screenshot +
 * timestamps)."
 *
 * **notification-svc sends the nudge, not the app.** What the client owns is the countdown either
 * party is shown — a passenger waiting on a confirmation, and a driver being told a claim is
 * outstanding — and the moment "Get help" becomes offerable.
 */
public object DriverQrAttestation {

    /** How long a claim waits before the driver is nudged again (BR-30.1). */
    public val NUDGE_DELAY: Duration = 5.minutes

    /** When the nudge for a claim made at [claimedAt] falls due. */
    public fun nudgeDueAt(claimedAt: Timestamp): Timestamp = claimedAt + NUDGE_DELAY

    /** Whether that moment has passed. */
    public fun isNudgeDue(claimedAt: Timestamp, now: Timestamp): Boolean = now >= nudgeDueAt(claimedAt)

    /** What is left before the nudge, floored at zero. */
    public fun remainingBeforeNudge(claimedAt: Timestamp, now: Timestamp): Duration {
        val left = nudgeDueAt(claimedAt) - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    /**
     * Whether "Get help" should be offered (BR-30.1).
     *
     * Only from [PaymentState.QrClaimedByPassenger] and only once the nudge has already gone
     * unanswered: before that the driver has simply not looked at their phone yet, and a support
     * ticket per unread push would bury the Finance queue.
     */
    public fun escalationAvailable(state: PaymentState, claimedAt: Timestamp?, now: Timestamp): Boolean =
        state == PaymentState.QrClaimedByPassenger && claimedAt != null && isNudgeDue(claimedAt, now)
}

/**
 * What the client currently believes about one payment.
 *
 * @property paymentId The payment row.
 * @property rideId The ride it settles.
 * @property state Where fare-svc last said it was.
 * @property method How it is being paid.
 * @property amountMinor The fare, minor units.
 * @property surchargeMinor The gateway surcharge, minor units (US-8.11).
 * @property tipMinor The gratuity, minor units (E-10).
 * @property claimedAt When the passenger claimed a driver-QR payment, for the +5-min nudge (AL-47).
 * @property settledAt When it reached a terminal state.
 */
public data class PaymentSnapshot(
    val paymentId: Ulid,
    val rideId: Ulid,
    val state: PaymentState,
    val method: PaymentMethod,
    val amountMinor: Long,
    val surchargeMinor: Long = 0,
    val tipMinor: Long = 0,
    val claimedAt: Timestamp? = null,
    val settledAt: Timestamp? = null,
) {

    /** The fare. */
    public val amount: Money get() = Money.ofMinor(amountMinor)

    /** What the passenger is actually charged: fare + surcharge + tip. */
    public val chargedTotal: Money get() = Money.ofMinor(amountMinor + surchargeMinor + tipMinor)

    /** Whether the payment can still move (C012's `PaymentState.isTerminal`). */
    public val isTerminal: Boolean get() = state.isTerminal

    /**
     * Whether the driver's earning has been released (R-05, extended by AL-47).
     *
     * True only in a state that *settles* — a refunded, partially refunded or disputed payment is
     * terminal without having paid anybody, so [isTerminal] is the wrong question to ask here.
     */
    public val isEarningReleased: Boolean get() = PaymentTransitions.settlementTrigger(state) != null &&
        state != PaymentState.Disputed

    public companion object {

        /** Projects the `PaymentStatus` every payment read answers with. */
        public fun of(status: PaymentStatus): PaymentSnapshot = PaymentSnapshot(
            paymentId = status.paymentId,
            rideId = status.rideId,
            state = status.state,
            method = status.method,
            amountMinor = status.amountMinor,
            surchargeMinor = status.surchargeMinor ?: 0,
            tipMinor = status.tipMinor ?: 0,
            settledAt = status.settledAt,
        )

        /** Projects the `POST /v1/fare/pay` response, which has no ride id of its own. */
        public fun of(initiation: PaymentInitiation, rideId: Ulid): PaymentSnapshot = PaymentSnapshot(
            paymentId = initiation.paymentId,
            rideId = rideId,
            state = initiation.state,
            method = initiation.method,
            amountMinor = initiation.amountMinor,
            surchargeMinor = initiation.surchargeMinor ?: 0,
        )
    }
}

/** Why a server frame changed nothing. */
public enum class PaymentUpdateIgnored {

    /** The same state, again. Ordinary: a poll and a push describe the same settlement. */
    DUPLICATE,

    /**
     * The frame would move a **terminal** payment somewhere else.
     *
     * A settled payment does not un-settle. In practice this is an in-flight poll answering after
     * the settling push has already landed; the poll's answer is simply older, and with no version
     * on `PaymentStatus` this is the only ordering rule available.
     */
    ALREADY_TERMINAL,
}

/** What a server frame did to the projection. */
public sealed interface PaymentUpdate {

    /**
     * Nothing changed.
     *
     * @property reason Why the frame was passed over.
     */
    public data class Ignored(val reason: PaymentUpdateIgnored) : PaymentUpdate

    /**
     * The projection moved.
     *
     * @property from Where it was.
     * @property to Where it is.
     * @property trigger The edge the server took, when [PaymentTransitions] draws exactly one
     *   between the two states; `null` when it draws none or more than one.
     */
    public data class Applied(val from: PaymentState, val to: PaymentState, val trigger: PaymentTrigger?) :
        PaymentUpdate {

        /**
         * Whether [PaymentTransitions] draws this move at all.
         *
         * `false` means the server and this build disagree about the machine — worth a log line
         * and a metric, never a reason to ignore fare-svc.
         */
        public val isKnownEdge: Boolean get() = PaymentTransitions.isReachable(from, to)

        /** Whether this move released the driver's earning (R-05). */
        public val releasesEarning: Boolean
            get() = PaymentTransitions.settlementTrigger(to) != null && to != PaymentState.Disputed
    }
}

/**
 * One payment, as the client understands it.
 *
 * @param initial Where the payment starts — from `POST /v1/fare/pay` or a status read.
 */
public class PaymentProjection(initial: PaymentSnapshot) {

    private val mutableState = MutableStateFlow(initial)

    /** Where the payment is. */
    public val snapshot: StateFlow<PaymentSnapshot> = mutableState.asStateFlow()

    /** The current state, for callers that want the one field. */
    public val state: PaymentState get() = mutableState.value.state

    /**
     * Applies a state **fare-svc** reported.
     *
     * @param status The server's answer.
     * @param observedAt When the client saw it. Used only to stamp [PaymentSnapshot.claimedAt] the
     *   first time a driver-QR claim appears, because `PaymentStatus` carries no claim timestamp of
     *   its own and the +5-minute nudge has to be measured from something (AL-47). The server's
     *   nudge runs off its own clock; this one only drives what the two apps display.
     */
    public fun onServerState(status: PaymentStatus, observedAt: Timestamp? = null): PaymentUpdate =
        onServerState(PaymentSnapshot.of(status), observedAt)

    /** Applies a projected snapshot — the `POST /v1/fare/pay` and claim/confirm responses. */
    public fun onServerState(next: PaymentSnapshot, observedAt: Timestamp? = null): PaymentUpdate {
        val current = mutableState.value
        ignoredReason(current, next)?.let { return PaymentUpdate.Ignored(it) }

        mutableState.value = next.copy(
            claimedAt = claimedAtFor(current, next, observedAt),
            // A frame that does not carry a settlement instant must not erase one we already have:
            // the claim/confirm responses answer with the payment, not with the ride's timeline.
            settledAt = next.settledAt ?: current.settledAt,
        )
        return PaymentUpdate.Applied(
            from = current.state,
            to = next.state,
            trigger = PaymentTransitions.triggerBetween(current.state, next.state),
        )
    }

    /**
     * Whether [trigger] is worth sending right now.
     *
     * A local guess that saves a round trip — explicitly **not** a claim about what fare-svc would
     * allow. The server re-checks everything and `409 payment-already-settled` is the real answer.
     */
    public fun canSend(trigger: PaymentTrigger): Boolean = PaymentTransitions.isLegal(state, trigger)

    /** Whether the driver's earning has been released (R-05, AL-47). */
    public val isEarningReleased: Boolean get() = mutableState.value.isEarningReleased

    /** When the +5-minute driver-QR nudge falls due, or `null` when no claim is outstanding. */
    public fun nudgeDueAt(): Timestamp? = mutableState.value
        .takeIf { it.state == PaymentState.QrClaimedByPassenger }
        ?.claimedAt
        ?.let(DriverQrAttestation::nudgeDueAt)

    /** Whether "Get help" should be offered on this payment right now (AL-47). */
    public fun escalationAvailable(now: Timestamp): Boolean = mutableState.value.let {
        DriverQrAttestation.escalationAvailable(it.state, it.claimedAt, now)
    }

    private fun ignoredReason(current: PaymentSnapshot, next: PaymentSnapshot): PaymentUpdateIgnored? = when {
        next.state == current.state -> PaymentUpdateIgnored.DUPLICATE
        current.isTerminal -> PaymentUpdateIgnored.ALREADY_TERMINAL
        else -> null
    }

    /**
     * When the outstanding claim was made.
     *
     * Stamped the first time the payment is seen in [PaymentState.QrClaimedByPassenger] and kept
     * until it leaves that state, so a poll landing four minutes later does not restart the
     * five-minute countdown.
     */
    private fun claimedAtFor(current: PaymentSnapshot, next: PaymentSnapshot, observedAt: Timestamp?): Timestamp? =
        when (next.state) {
            PaymentState.QrClaimedByPassenger -> next.claimedAt ?: current.claimedAt ?: observedAt
            else -> null
        }
}
