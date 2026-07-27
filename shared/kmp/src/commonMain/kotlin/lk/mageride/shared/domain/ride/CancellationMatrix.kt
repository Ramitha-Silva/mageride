package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.RideState

// The D5' §7 cancellation & no-show matrix, projected for the client.
//
// ride-svc is the SOLE WRITER of rides.state and the sole author of a penalty (R-03, D-05). What
// this file is for is telling a passenger what a tap is about to cost *before* they make it, and
// telling a driver why an offer went away. Every number here is mirrored from §7; none of it is
// charged, collected or decided on the device.

/**
 * What a cancellation costs the party that caused it.
 *
 * Three shapes rather than a nullable [Money], because "nothing" and "the whole fare" are
 * genuinely different from "Rs 50" — a screen renders them differently and the mid-trip case has
 * no amount to render until fare-svc has finalised one.
 */
public sealed interface CancellationCost {

    /** No charge. Every pre-acceptance cancel, and every offer decline or expiry (US-6A.9). */
    public data object None : CancellationCost

    /**
     * A fixed amount.
     *
     * @property amount Rs 50 after acceptance (D-05), Rs 100 for a rider no-show (D5' §7).
     */
    public data class Flat(val amount: Money) : CancellationCost

    /**
     * The whole fare — a rider cancelling once the ride is under way (D5' §7).
     *
     * Carries no amount: the meter is still running, and fare-svc computes the figure from the
     * Kalman-filtered distance at the moment the cancel lands (E-04).
     */
    public data object FullFare : CancellationCost
}

/**
 * One row of D5' §7.
 *
 * @property to The terminal — or, for an offer decline, the state the ride returns to.
 * @property cost What the acting party is charged.
 * @property countsTowardBookingDisable Whether this increments `cancellations_continuous`
 *   (§7.2 — **post-acceptance passenger cancels only**; a no-show is not a cancel and a
 *   pre-acceptance cancel never counts).
 * @property releasesDriver Whether the driver goes back in the pool. The matrix's "Driver avail"
 *   column: everything but a pre-acceptance cancel, which had no driver to release.
 * @property driverReputationHit Whether reputation-svc records this against the driver.
 * @property domainEvent The `rides.outbox` event §7 names for this row. Not emitted here — it is
 *   what a client correlates a push or a SignalR frame against.
 */
public data class CancellationOutcome(
    val to: RideState,
    val cost: CancellationCost,
    val countsTowardBookingDisable: Boolean = false,
    val releasesDriver: Boolean = false,
    val driverReputationHit: Boolean = false,
    val domainEvent: String,
)

/**
 * D5' §7, as a lookup.
 *
 * Keyed on `(from, trigger)` exactly like [RideTransitions], and every [CancellationOutcome.to]
 * here is the state that table already draws — [outcomeOf] asserts it, so a row that disagreed
 * with the state machine would fail at first use rather than mislead a screen.
 */
public object CancellationMatrix {

    /** Rs 50 as the integer minor units everything on the platform is stored in. */
    private const val AFTER_ACCEPTANCE_PENALTY_MINOR: Long = 5_000

    /** Rs 100, likewise. */
    private const val RIDER_NO_SHOW_PENALTY_MINOR: Long = 10_000

    /** The cross-trip cancellation penalty, Rs 50 (D-05, AL-16, §7.1). */
    public val AFTER_ACCEPTANCE_PENALTY: Money = Money.ofMinor(AFTER_ACCEPTANCE_PENALTY_MINOR)

    /** The rider no-show penalty, Rs 100 (§7). The driver is compensated half the base fare. */
    public val RIDER_NO_SHOW_PENALTY: Money = Money.ofMinor(RIDER_NO_SHOW_PENALTY_MINOR)

    /**
     * Consecutive post-acceptance cancels that disable booking (US-6A.10b, AL-16, §7.2).
     *
     * Consecutive, not cumulative: **any completed ride resets the counter to zero**.
     */
    public const val CONSECUTIVE_CANCEL_LIMIT: Int = 3

    private val ROWS: Map<Pair<RideState, RideTrigger>, CancellationOutcome> = buildMap {
        for (from in listOf(RideState.Requested, RideState.Matching, RideState.Offered)) {
            put(
                from to RideTrigger.RIDER_CANCELLED,
                CancellationOutcome(
                    to = RideState.CancelledByRiderBeforeAccept,
                    cost = CancellationCost.None,
                    releasesDriver = from == RideState.Offered,
                    domainEvent = "ride.cancelled",
                ),
            )
        }

        put(
            RideState.Matching to RideTrigger.NO_DRIVER_FOUND,
            CancellationOutcome(
                to = RideState.ExpiredNoDriver,
                cost = CancellationCost.None,
                domainEvent = "ride.expired_no_driver",
            ),
        )
        put(
            RideState.Offered to RideTrigger.OFFER_DECLINED,
            CancellationOutcome(
                to = RideState.Matching,
                cost = CancellationCost.None,
                releasesDriver = true,
                domainEvent = "offer.declined",
            ),
        )
        put(
            RideState.Offered to RideTrigger.OFFER_EXPIRED,
            CancellationOutcome(
                to = RideState.Matching,
                cost = CancellationCost.None,
                releasesDriver = true,
                domainEvent = "offer.expired",
            ),
        )

        put(
            RideState.Accepted to RideTrigger.RIDER_CANCELLED,
            CancellationOutcome(
                to = RideState.CancelledByRiderAfterAccept,
                cost = CancellationCost.Flat(AFTER_ACCEPTANCE_PENALTY),
                countsTowardBookingDisable = true,
                releasesDriver = true,
                domainEvent = "ride.cancelled",
            ),
        )
        put(
            RideState.InProgress to RideTrigger.RIDER_CANCELLED,
            CancellationOutcome(
                to = RideState.CancelledByRiderAfterAccept,
                cost = CancellationCost.FullFare,
                countsTowardBookingDisable = true,
                releasesDriver = true,
                domainEvent = "ride.cancelled",
            ),
        )

        for (from in listOf(RideState.Accepted, RideState.DriverArrived, RideState.InProgress)) {
            put(
                from to RideTrigger.DRIVER_CANCELLED,
                CancellationOutcome(
                    to = RideState.CancelledByDriver,
                    cost = CancellationCost.None,
                    releasesDriver = true,
                    driverReputationHit = true,
                    domainEvent = "reputation.driver_cancelled",
                ),
            )
        }
        for (from in listOf(RideState.Accepted, RideState.DriverArrived)) {
            put(
                from to RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED,
                CancellationOutcome(
                    to = RideState.CancelledByDriver,
                    cost = CancellationCost.None,
                    releasesDriver = true,
                    driverReputationHit = true,
                    domainEvent = "reputation.driver_cancelled",
                ),
            )
            put(
                from to RideTrigger.DRIVER_NO_SHOW,
                CancellationOutcome(
                    to = RideState.NoShowDriver,
                    cost = CancellationCost.None,
                    releasesDriver = true,
                    driverReputationHit = true,
                    domainEvent = "ride.no_show_driver",
                ),
            )
        }
        for (from in listOf(RideState.InProgress, RideState.PaymentPending)) {
            put(
                from to RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED,
                CancellationOutcome(
                    to = RideState.Disputed,
                    cost = CancellationCost.None,
                    releasesDriver = true,
                    domainEvent = "ride.disputed",
                ),
            )
        }

        put(
            RideState.DriverArrived to RideTrigger.RIDER_NO_SHOW,
            CancellationOutcome(
                to = RideState.NoShowRider,
                cost = CancellationCost.Flat(RIDER_NO_SHOW_PENALTY),
                releasesDriver = true,
                domainEvent = "ride.no_show_rider",
            ),
        )
    }

    /**
     * The §7 row for this move, or `null` when the move is not a cancellation at all.
     *
     * @throws IllegalStateException if a row and [RideTransitions] disagree about where the ride
     *   ends up. That is a bug in this file, not something a caller can cause.
     */
    public fun outcomeOf(from: RideState, trigger: RideTrigger): CancellationOutcome? {
        val row = ROWS[from to trigger] ?: return null
        val expected = RideTransitions.next(from, trigger)
        check(row.to == expected) { "§7 row $from/$trigger says ${row.to}; Appendix B.2 says $expected" }
        return row
    }

    /** What a rider cancelling **right now** would be charged, for the confirmation sheet. */
    public fun costOfRiderCancelling(from: RideState): CancellationCost? =
        outcomeOf(from, RideTrigger.RIDER_CANCELLED)?.cost
}

/**
 * A passenger's cancellation standing — the Rs 50 debt and the run-up to a booking block
 * (AL-16, D5' §7.1/§7.2).
 *
 * **This is a mirror, not a ledger.** `dispatch.cancellation_penalties` holds the debt and
 * `reputation.counters.cancellations_continuous` holds the count; both settle server-side and both
 * arrive on the next read. Keeping the projection lets the passenger app say "cancelling now costs
 * Rs 50, and it would be your third — booking would be disabled" *before* the tap, which is the
 * only place a client-side copy earns its keep.
 *
 * The debt is **cross-trip**: nothing is charged at cancellation time, because there is no card on
 * file. Rs 50 is added to the passenger's next completed trip's fare and paid through that trip's
 * driver to the driver who was stood up (§7.1, AL-16).
 *
 * @property outstandingBalance What the passenger owes, to be added to the next trip's fare.
 * @property consecutivePostAcceptanceCancels Post-acceptance cancels since the last completed
 *   ride. Pre-acceptance cancels never touch it (§7.2).
 * @property serverBookingDisabled reputation-svc's own answer, when the client has one. It
 *   **wins** over the local count in both directions: re-enablement needs the balance cleared
 *   *and* a cooldown or a CSR reinstatement (§7.2), which is not something a device can work out.
 */
public data class PassengerStanding(
    val outstandingBalance: Money = Money.ZERO,
    val consecutivePostAcceptanceCancels: Int = 0,
    val serverBookingDisabled: Boolean? = null,
) {

    /** Whether the booking entry point is blocked (`403 booking-disabled` on the way in). */
    public val isBookingDisabled: Boolean
        get() = serverBookingDisabled
            ?: (consecutivePostAcceptanceCancels >= CancellationMatrix.CONSECUTIVE_CANCEL_LIMIT)

    /** Whether there is a debt to show at all. */
    public val hasOutstandingBalance: Boolean get() = outstandingBalance > Money.ZERO

    /** Post-acceptance cancels left before booking is disabled. Zero once it is (US-6A.10b). */
    public val cancelsBeforeBookingDisabled: Int
        get() = (CancellationMatrix.CONSECUTIVE_CANCEL_LIMIT - consecutivePostAcceptanceCancels).coerceAtLeast(0)

    /**
     * The standing after a cancellation described by [outcome].
     *
     * Only a [CancellationCost.Flat] cost accrues a balance: a full-fare mid-trip cancel is
     * charged on that trip's own payment, not carried to the next one.
     */
    public fun afterCancellation(outcome: CancellationOutcome): PassengerStanding {
        val accrued = (outcome.cost as? CancellationCost.Flat)?.amount ?: Money.ZERO
        return copy(
            outstandingBalance = outstandingBalance + accrued,
            consecutivePostAcceptanceCancels = consecutivePostAcceptanceCancels +
                if (outcome.countsTowardBookingDisable) 1 else 0,
        )
    }

    /**
     * The standing after a completed ride.
     *
     * Two independent effects of one event (§7.1, §7.2): **every** outstanding penalty settles on
     * this trip — §7.1 loops over them, so a passenger who owes Rs 100 pays Rs 100 — and the
     * consecutive counter resets to zero. [serverBookingDisabled] is deliberately left alone:
     * re-enablement is reputation-svc's call.
     */
    public fun afterCompletedRide(): PassengerStanding =
        copy(outstandingBalance = Money.ZERO, consecutivePostAcceptanceCancels = 0)
}
