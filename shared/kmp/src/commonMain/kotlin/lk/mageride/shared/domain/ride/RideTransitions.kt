package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.RideState

// The Mode C ride aggregate's transition table, client side.
//
// Source: ADD Appendix B.2 (v2.1/v2.2/v2.4) + D5' §6 (the same machine as mermaid) + D5' §7 (the
// cancellation & no-show matrix, which is where several edges are actually spelled out).
//
// THIS IS MODE C ONLY (R-01). Mode A/B tracking sessions belong to trip-state-svc and their state
// machine is ADD Appendix B — a different aggregate with a different owner. Nothing in this
// package models a tracking session, and nothing ever will.
//
// THE SERVER OWNS THE STATE; THIS TABLE ONLY KNOWS ITS SHAPE. ride-svc is the sole writer (R-01)
// and every move it makes is versioned (R-14). What the table is for is deciding, locally and
// without a round trip, whether a command is worth sending and whether a state the server just
// reported is one the client understands. It never *advances* a ride on its own — see
// [RideProjection].

/**
 * Who pulls a trigger.
 *
 * On a proxy booking (P-01) the [RIDER] side is the **booker**: they made the booking, they hold
 * the cancel, and the rider may not even have an account. On a package (P-06) there is no rider at
 * all and [RIDER] is the sender.
 */
public enum class RideActor {

    /** The passenger, or on a proxy booking the booker who made it. */
    RIDER,

    /** The driver holding the offer or the ride. */
    DRIVER,

    /**
     * ride-svc, dispatch-svc or a timer.
     *
     * No app can send one of these. They arrive as a state change over SignalR
     * (`RideStateChanged`) or on the next read, which is the only way a client learns of them.
     */
    SYSTEM,
}

/**
 * A labelled edge of ADD Appendix B.2 — the *reason* a ride moved, not the API call that caused
 * it.
 *
 * One trigger can land in different states depending on where it fires: [RIDER_CANCELLED] before
 * acceptance is free and after it costs Rs 50 (D5' §7), and [DRIVER_OFFLINE_GRACE_EXPIRED] ends a
 * pre-pickup ride but disputes an in-progress one (R-16). That is why the table is keyed on
 * `(from, trigger)` rather than on the trigger alone.
 *
 * @property actor Who can cause it.
 */
public enum class RideTrigger(public val actor: RideActor) {

    /** dispatch-svc started building candidates (D5' §3.1). */
    DISPATCH_STARTED(RideActor.SYSTEM),

    /** No candidate accepted within the rounds, or the 2-minute global timeout fired (US-6A.11). */
    NO_DRIVER_FOUND(RideActor.SYSTEM),

    /** A candidate was reserved and the offer pushed, under a 15-second TTL (D5' §3.5). */
    OFFER_PUSHED(RideActor.SYSTEM),

    /** The driver passed on the offer. No penalty; the cascade moves to the next candidate. */
    OFFER_DECLINED(RideActor.DRIVER),

    /** The 15 seconds elapsed. Same outcome as a decline, and no penalty either (D5' §7). */
    OFFER_EXPIRED(RideActor.SYSTEM),

    /** The driver won the atomic single-winner accept (R-02, D5' §6.1). */
    OFFER_ACCEPTED(RideActor.DRIVER),

    /** The rider — or the proxy booker — cancelled. What that costs depends on where (D5' §7). */
    RIDER_CANCELLED(RideActor.RIDER),

    /** The driver cancelled after accepting. Reputation hit and a brief delisting (D5' §7). */
    DRIVER_CANCELLED(RideActor.DRIVER),

    /**
     * The driver went offline and stayed offline past this state's grace window (R-15/R-16).
     *
     * The grace is per-state and so is the outcome — see [RideGrace].
     */
    DRIVER_OFFLINE_GRACE_EXPIRED(RideActor.SYSTEM),

    /** The driver entered the pickup geofence, or tapped Arrived. */
    DRIVER_ARRIVED(RideActor.DRIVER),

    /** The rider was not at the pickup five minutes after arrival: Rs 100 (D5' §7). */
    RIDER_NO_SHOW(RideActor.SYSTEM),

    /** The driver accepted but never reached the pickup and the rider's wait ran out (D5' §7). */
    DRIVER_NO_SHOW(RideActor.SYSTEM),

    /** The driver tapped Start — with the rider's OTP, or the package pickup OTP (P-07). */
    RIDE_STARTED(RideActor.DRIVER),

    /** The driver tapped Complete — or the package's delivery handoff finished (P-07/P-10). */
    RIDE_COMPLETED(RideActor.DRIVER),

    /** fare-svc finalised the fare; settlement is now outstanding (R-05). */
    FARE_FINALISED(RideActor.SYSTEM),

    /** The gateway confirmed the payment (D-10). */
    PAYMENT_SUCCEEDED(RideActor.SYSTEM),

    /** Settled in cash, including the fallback from a failed digital payment. */
    CASH_SETTLED(RideActor.SYSTEM),

    /** The driver confirmed cash on delivery on a package ride (P-08). */
    COD_COLLECTED(RideActor.DRIVER),

    /** A dispute was opened, or a late callback overpaid a settled ride (E-05, §11.14). */
    DISPUTE_RAISED(RideActor.RIDER),
}

/**
 * One edge: from [from], pulling [trigger], the aggregate lands in [to].
 *
 * @property from Where the ride was.
 * @property trigger Why it moved.
 * @property to Where it went.
 */
public data class RideEdge(val from: RideState, val trigger: RideTrigger, val to: RideState)

/**
 * ADD Appendix B.2, as data.
 *
 * The table is deliberately the *whole* contract of this class: [next] is a map lookup and there
 * is no branch anywhere that can produce a state the table does not list. `RideTransitionTableTest`
 * re-declares Appendix B.2 independently and asserts the two agree edge for edge, which is what
 * makes "no transition outside Appendix B.2 is reachable" a property rather than a comment.
 *
 * **Two edges are not drawn in the Appendix B.2 diagram** and are here because the prose and the
 * matrix put them there. Both are called out at the declaration:
 * - `Matching → Accepted`, from D5' §6.1's accept guard `state IN ('Matching','Offered')`.
 * - `Accepted|DriverArrived → NoShowDriver`, from the D5' §7 matrix row.
 */
public object RideTransitions {

    /**
     * The states a rider can still walk away from for free (US-6A.9).
     *
     * Not derived from `isDriverAssigned`: [RideState.Requested] and [RideState.Matching] are
     * unassigned too, but so is `ExpiredNoDriver`, and cancelling a terminal ride is not a thing.
     */
    private val PRE_ACCEPTANCE: List<RideState> =
        listOf(RideState.Requested, RideState.Matching, RideState.Offered)

    /** The live states in which a driver is on the hook for the ride. */
    private val DRIVER_HELD: List<RideState> =
        listOf(RideState.Accepted, RideState.DriverArrived, RideState.InProgress)

    /**
     * Every legal `(from, trigger) → to`.
     *
     * Grouped the way ADD Appendix B.2 reads: down the happy path first, then the cancellation and
     * no-show matrix (D5' §7), then settlement.
     */
    public val EDGES: Set<RideEdge> = buildSet {
        // ---- dispatch ------------------------------------------------------------------------
        add(RideEdge(RideState.Requested, RideTrigger.DISPATCH_STARTED, RideState.Matching))
        add(RideEdge(RideState.Matching, RideTrigger.NO_DRIVER_FOUND, RideState.ExpiredNoDriver))
        add(RideEdge(RideState.Matching, RideTrigger.OFFER_PUSHED, RideState.Offered))
        add(RideEdge(RideState.Offered, RideTrigger.OFFER_DECLINED, RideState.Matching))
        add(RideEdge(RideState.Offered, RideTrigger.OFFER_EXPIRED, RideState.Matching))
        add(RideEdge(RideState.Offered, RideTrigger.OFFER_ACCEPTED, RideState.Accepted))

        // D5' §6.1's conditional UPDATE guards on `state IN ('Matching','Offered')`, not on
        // `Offered` alone: the 15-second TTL can bounce the ride back to Matching while the
        // winning accept is still in flight, and the offer row it names is still the current one.
        // The Appendix B.2 diagram draws only the Offered edge. Dropping this one would make the
        // driver app's own successful accept look like a state the client does not understand.
        add(RideEdge(RideState.Matching, RideTrigger.OFFER_ACCEPTED, RideState.Accepted))

        // ---- the ride ----------------------------------------------------------------------
        add(RideEdge(RideState.Accepted, RideTrigger.DRIVER_ARRIVED, RideState.DriverArrived))
        add(RideEdge(RideState.DriverArrived, RideTrigger.RIDE_STARTED, RideState.InProgress))
        add(RideEdge(RideState.InProgress, RideTrigger.RIDE_COMPLETED, RideState.Completed))
        add(RideEdge(RideState.Completed, RideTrigger.FARE_FINALISED, RideState.PaymentPending))

        // ---- cancellation & no-show (D5' §7) -------------------------------------------------
        // "Any pre-Accepted state + rider cancel → CancelledByRiderBeforeAccept" (Appendix B.2).
        for (from in PRE_ACCEPTANCE) {
            add(RideEdge(from, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderBeforeAccept))
        }
        add(RideEdge(RideState.Accepted, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderAfterAccept))
        add(RideEdge(RideState.InProgress, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderAfterAccept))

        // "Any post-Accepted state + driver cancel / LWT offline beyond grace → CancelledByDriver"
        // (Appendix B.2). The D5' §7 matrix names Accepted and DriverArrived explicitly; the
        // catch-all is what covers a driver abandoning a ride already under way.
        for (from in DRIVER_HELD) {
            add(RideEdge(from, RideTrigger.DRIVER_CANCELLED, RideState.CancelledByDriver))
        }
        add(RideEdge(RideState.Accepted, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.CancelledByDriver))
        add(RideEdge(RideState.DriverArrived, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.CancelledByDriver))
        add(RideEdge(RideState.InProgress, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.Disputed))
        add(RideEdge(RideState.PaymentPending, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.Disputed))

        add(RideEdge(RideState.DriverArrived, RideTrigger.RIDER_NO_SHOW, RideState.NoShowRider))

        // The D5' §7 row "Accepted/DriverArrived | driver never reaches pickup, grace exceeded |
        // NoShowDriver". Appendix B.2's diagram does not draw it; the matrix and the
        // `ck_rides_state` CHECK (C004) both carry the state, so it is here. See the C015 handoff.
        add(RideEdge(RideState.Accepted, RideTrigger.DRIVER_NO_SHOW, RideState.NoShowDriver))
        add(RideEdge(RideState.DriverArrived, RideTrigger.DRIVER_NO_SHOW, RideState.NoShowDriver))

        // ---- settlement (R-05, D-10, P-08) ---------------------------------------------------
        add(RideEdge(RideState.PaymentPending, RideTrigger.PAYMENT_SUCCEEDED, RideState.Paid))
        add(RideEdge(RideState.PaymentPending, RideTrigger.CASH_SETTLED, RideState.CashSettled))
        add(RideEdge(RideState.PaymentPending, RideTrigger.COD_COLLECTED, RideState.CashOnDeliveryCollected))
        add(RideEdge(RideState.PaymentPending, RideTrigger.DISPUTE_RAISED, RideState.Disputed))
    }

    private val BY_KEY: Map<Pair<RideState, RideTrigger>, RideState> =
        EDGES.associate { (it.from to it.trigger) to it.to }

    private val BY_STATE: Map<RideState, Set<RideTrigger>> =
        EDGES.groupBy { it.from }.mapValues { (_, edges) -> edges.mapTo(mutableSetOf()) { it.trigger } }

    /** Where [trigger] lands from [from], or `null` when the table draws no such edge. */
    public fun next(from: RideState, trigger: RideTrigger): RideState? = BY_KEY[from to trigger]

    /** Whether the table draws this edge at all. */
    public fun isLegal(from: RideState, trigger: RideTrigger): Boolean = BY_KEY.containsKey(from to trigger)

    /** Whether the table draws an edge from [from] to [to], under any trigger. */
    public fun isReachable(from: RideState, to: RideState): Boolean = EDGES.any { it.from == from && it.to == to }

    /** Every trigger that does something from [from]. Empty for every terminal state. */
    public fun triggersFrom(from: RideState): Set<RideTrigger> = BY_STATE[from].orEmpty()

    /** Every state [from] can move to in one step. */
    public fun successorsOf(from: RideState): Set<RideState> =
        EDGES.filterTo(mutableSetOf()) { it.from == from }.mapTo(mutableSetOf()) { it.to }
}
