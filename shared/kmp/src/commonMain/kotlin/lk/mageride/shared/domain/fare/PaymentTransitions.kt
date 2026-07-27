package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.domain.ride.RideTransitions
import lk.mageride.shared.domain.ride.RideTrigger

// The ride-payment state machine, client side (D-10, ADD §11.8, D5' §8.1, AL-47, P-08, R-19).
//
// FARE-SVC IS THE SOLE WRITER. `fares.ride_payments.state` moves on a gateway callback, on a
// driver's or passenger's attestation, or on a timer — never on a client's say-so. This table
// exists for the same three reasons [lk.mageride.shared.domain.ride.RideTransitions] does: to say
// whether a button is worth tapping, to name the edge the server took when a status arrives, and
// to make "the client understands this machine" a property a test can check rather than a comment.
//
// FOUR EDGES ARE NOT DRAWN IN THE §8.1 MERMAID and are here because other parts of the same spec
// put them there. Each is commented at its declaration and each is listed separately in
// `PaymentTransitionTableTest`, so the claim stays auditable:
//   (a) Initiated -> FellBackToCash          — a `cash` ride never touches a gateway (§8.1 methods)
//   (b) Initiated -> CashOnDelivery          — a COD package never touches one either (§8.3)
//   (c) Succeeded -> Refunded/PartiallyRefunded — the E-05 admin reversal (§8.2)
//   (d) CashOnDelivery -> Disputed           — the 24 h `cod_uncollected` timer (§8.3, P-14)
//
// NAMING DRIFT WORTH KNOWING: R-05 and D5' §8.1 say the earning posts on "Paid / CashSettled /
// CashOnDeliveryCollected". `Paid` and `CashSettled` are RIDE states (RideState), not payment
// states — the payment-side spellings are `Succeeded` and `FellBackToCash`. [settlementTrigger]
// is the mapping between the two machines.

/**
 * Why a payment moved.
 *
 * Keyed like a ride trigger: one trigger can land in different places depending on where it fires,
 * which is why [PaymentTransitions] is keyed on `(from, trigger)` and not on the trigger alone.
 *
 * @property actor Who causes it. No app can send a [PaymentActor.SYSTEM] one — those arrive as a
 *   `PaymentStatus` on the next read or as a push.
 */
public enum class PaymentTrigger(public val actor: PaymentActor) {

    /** The passenger committed to a gateway method and fare-svc handed off (§8.1, 90 s timeout). */
    GATEWAY_HANDOFF(PaymentActor.PASSENGER),

    /** OnePay or the bank IPG confirmed. **OnePay-only since AL-47** for the QR path (D-10). */
    PROVIDER_SUCCEEDED(PaymentActor.SYSTEM),

    /** The gateway declined, errored or timed out (§8.1). */
    PROVIDER_FAILED(PaymentActor.SYSTEM),

    /**
     * The passenger retried a failed payment (US-8.15).
     *
     * The retry is a **new row** chained by `retry_of_payment_id`; this row is finished. That is
     * why `Retried` has no outgoing edge — the machine continues on the successor, not here.
     */
    PASSENGER_RETRIED(PaymentActor.PASSENGER),

    /**
     * Settled in cash in the vehicle.
     *
     * Two situations, one edge: the US-8.15 fallback after three failed retries or a driver
     * override, and a ride booked `cash` in the first place, which is the default method and never
     * had a gateway leg at all.
     */
    SETTLED_IN_CASH(PaymentActor.DRIVER),

    /** A package booked `cod`: the driver collects on delivery (P-08). */
    COD_SELECTED(PaymentActor.SYSTEM),

    /** The driver tapped "Cash received" on a COD package (P-08, §8.3). */
    COD_COLLECTED(PaymentActor.DRIVER),

    /** The 24 h `cod_uncollected` timer fired and nobody could be reached (§8.3, P-14). */
    COD_UNCOLLECTED_TIMEOUT(PaymentActor.SYSTEM),

    /** A provider callback arrived **after** the ride had settled in cash (R-19, §11.14). */
    LATE_PROVIDER_CALLBACK(PaymentActor.SYSTEM),

    /** Finance reversed the whole payment (E-05). */
    REFUND_COMPLETED(PaymentActor.SYSTEM),

    /** Finance reversed part of it (E-05, `fares.refunds.kind='partial'`). */
    PARTIAL_REFUND_COMPLETED(PaymentActor.SYSTEM),

    /** The passenger says they paid by scanning the driver's QR (AL-47, US-26.1). */
    QR_CLAIMED_BY_PASSENGER(PaymentActor.PASSENGER),

    /**
     * The driver attests the QR money arrived (AL-47). **Terminal — the earning posts** (R-05).
     *
     * Valid with or without a prior claim: the driver's bank app is the only party that actually
     * saw the money, and a passenger who never tapped "I've paid" has not stopped them being paid.
     */
    QR_CONFIRMED_BY_DRIVER(PaymentActor.DRIVER),

    /** A claim nobody confirmed, escalated to the Finance dispute queue (AL-47). */
    DISPUTE_RAISED(PaymentActor.PASSENGER),
}

/** Who pulls a payment trigger. */
public enum class PaymentActor {

    /** The payer — the rider, or on a proxy booking the booker (P-04). */
    PASSENGER,

    /** The driver collecting cash, COD, or confirming a QR receipt. */
    DRIVER,

    /** A gateway callback, a timer, or Finance. No app sends one. */
    SYSTEM,
}

/**
 * One edge: from [from], pulling [trigger], the payment lands in [to].
 *
 * @property from Where the payment was.
 * @property trigger Why it moved.
 * @property to Where it went.
 */
public data class PaymentEdge(val from: PaymentState, val trigger: PaymentTrigger, val to: PaymentState)

/**
 * The D-10 machine, as data.
 *
 * The table is the whole contract of this object: [next] is a map lookup and nothing branches its
 * way to a state the table does not list.
 */
public object PaymentTransitions {

    /**
     * Every legal `(from, trigger) → to`.
     *
     * Grouped as §8.1 reads: the gateway path, then the cash and COD terminals, then the AL-47
     * attestation pair, then the R-19/E-05 reversals.
     */
    public val EDGES: Set<PaymentEdge> = buildSet {
        // ---- gateway path (§8.1) --------------------------------------------------------------
        add(PaymentEdge(PaymentState.Initiated, PaymentTrigger.GATEWAY_HANDOFF, PaymentState.Pending))
        add(PaymentEdge(PaymentState.Pending, PaymentTrigger.PROVIDER_SUCCEEDED, PaymentState.Succeeded))
        add(PaymentEdge(PaymentState.Pending, PaymentTrigger.PROVIDER_FAILED, PaymentState.Failed))
        add(PaymentEdge(PaymentState.Failed, PaymentTrigger.PASSENGER_RETRIED, PaymentState.Retried))
        add(PaymentEdge(PaymentState.Failed, PaymentTrigger.SETTLED_IN_CASH, PaymentState.FellBackToCash))

        // Not in the §8.1 diagram. Cash is the DEFAULT method (§8.1 "Cash (default, driver
        // collects)") and a cash ride has no gateway leg to fail first, so without this edge the
        // most common settlement on the platform would be an unknown transition.
        add(PaymentEdge(PaymentState.Initiated, PaymentTrigger.SETTLED_IN_CASH, PaymentState.FellBackToCash))

        // ---- cash on delivery (P-08, §8.3) ----------------------------------------------------
        add(PaymentEdge(PaymentState.Pending, PaymentTrigger.COD_SELECTED, PaymentState.CashOnDelivery))
        add(
            PaymentEdge(
                PaymentState.CashOnDelivery,
                PaymentTrigger.COD_COLLECTED,
                PaymentState.CashOnDeliveryCollected,
            ),
        )

        // Not in the diagram, for the same reason as the cash edge: `Pending` means a gateway
        // round trip is outstanding and a COD package never starts one. §8.3 says only
        // "`CashOnDelivery` set at delivery", which is reached from wherever the row already is.
        add(PaymentEdge(PaymentState.Initiated, PaymentTrigger.COD_SELECTED, PaymentState.CashOnDelivery))

        // Not in the diagram: §8.3's Quartz `cod_uncollected` timer, 24 h → Disputed (P-14).
        add(PaymentEdge(PaymentState.CashOnDelivery, PaymentTrigger.COD_UNCOLLECTED_TIMEOUT, PaymentState.Disputed))

        // ---- driver-QR attestation (AL-47) ----------------------------------------------------
        add(
            PaymentEdge(
                PaymentState.Initiated,
                PaymentTrigger.QR_CLAIMED_BY_PASSENGER,
                PaymentState.QrClaimedByPassenger,
            ),
        )
        add(
            PaymentEdge(
                PaymentState.QrClaimedByPassenger,
                PaymentTrigger.QR_CONFIRMED_BY_DRIVER,
                PaymentState.DriverConfirmedQR,
            ),
        )

        // "A driver confirm is valid without a prior claim" (BR-30.1) — the driver's bank app is
        // the only party that saw the money.
        add(
            PaymentEdge(
                PaymentState.Initiated,
                PaymentTrigger.QR_CONFIRMED_BY_DRIVER,
                PaymentState.DriverConfirmedQR,
            ),
        )

        // "Claim without confirm: nudge at +5 min; unresolved → Support ticket → Finance dispute
        // queue" (BR-30.1). No wallet movement — the platform holds none of this money.
        add(PaymentEdge(PaymentState.QrClaimedByPassenger, PaymentTrigger.DISPUTE_RAISED, PaymentState.Disputed))

        // A driver-QR ride can still end in cash if both parties settle that way (BR-30.1).
        add(PaymentEdge(PaymentState.QrClaimedByPassenger, PaymentTrigger.SETTLED_IN_CASH, PaymentState.FellBackToCash))

        // ---- late callback & refunds (R-19, E-05, §11.14) -------------------------------------
        add(PaymentEdge(PaymentState.FellBackToCash, PaymentTrigger.LATE_PROVIDER_CALLBACK, PaymentState.Overpaid))
        add(PaymentEdge(PaymentState.Overpaid, PaymentTrigger.REFUND_COMPLETED, PaymentState.Refunded))

        // Not in the diagram: §8.2's admin-initiated full/partial reversal of a *successful*
        // payment, which is where `fares.refunds.kind='full'|'partial'` and the `PartiallyRefunded`
        // state come from (C005 note (b)).
        add(PaymentEdge(PaymentState.Succeeded, PaymentTrigger.REFUND_COMPLETED, PaymentState.Refunded))
        add(
            PaymentEdge(
                PaymentState.Succeeded,
                PaymentTrigger.PARTIAL_REFUND_COMPLETED,
                PaymentState.PartiallyRefunded,
            ),
        )
        add(PaymentEdge(PaymentState.Succeeded, PaymentTrigger.DISPUTE_RAISED, PaymentState.Disputed))
    }

    private val byKey: Map<Pair<PaymentState, PaymentTrigger>, PaymentState> =
        EDGES.associate { (it.from to it.trigger) to it.to }

    private val byState: Map<PaymentState, Set<PaymentTrigger>> =
        EDGES.groupBy { it.from }.mapValues { (_, edges) -> edges.mapTo(mutableSetOf()) { it.trigger } }

    /** Where [trigger] lands from [from], or `null` when the table draws no such edge. */
    public fun next(from: PaymentState, trigger: PaymentTrigger): PaymentState? = byKey[from to trigger]

    /** Whether the table draws this edge at all. */
    public fun isLegal(from: PaymentState, trigger: PaymentTrigger): Boolean = byKey.containsKey(from to trigger)

    /** Whether the table draws an edge from [from] to [to], under any trigger. */
    public fun isReachable(from: PaymentState, to: PaymentState): Boolean = EDGES.any { it.from == from && it.to == to }

    /** Every trigger that does something from [from]. Empty for a state the machine cannot leave. */
    public fun triggersFrom(from: PaymentState): Set<PaymentTrigger> = byState[from].orEmpty()

    /**
     * The trigger connecting two states, if the table draws exactly one.
     *
     * `null` when it draws none **and** when it draws more than one: `Initiated →
     * DriverConfirmedQR` is only ever the driver's confirm, but a bare status frame that moved
     * `Failed → FellBackToCash` cannot say whether three retries ran out or the driver overrode.
     * Naming one of them would be a guess.
     */
    public fun triggerBetween(from: PaymentState, to: PaymentState): PaymentTrigger? =
        EDGES.filter { it.from == from && it.to == to }.singleOrNull()?.trigger

    /**
     * How a terminal payment settles the **ride** aggregate (R-05).
     *
     * The two machines are separate and their vocabularies differ. `POST
     * /v1/internal/rides/{rideId}/payment-settled` is how fare-svc tells ride-svc which of these
     * happened; this is the client's copy of that mapping, so a passenger app can predict the ride
     * state its next frame will carry instead of showing "payment done, ride still pending".
     *
     * `null` for every state that is not a settlement: a refund, a dispute or a non-terminal state
     * does not move the ride, and a `Disputed` payment reaches the ride through its own
     * `DISPUTE_RAISED` edge rather than through settlement.
     *
     * **[PaymentState.DriverConfirmedQR] maps to `CASH_SETTLED`** on the strength of AL-47's own
     * words — driver-QR "is settled like cash", bank-to-bank, with no platform leg. No spec names
     * the resulting `RideState` outright; see the C016 handoff.
     */
    public fun settlementTrigger(state: PaymentState): RideTrigger? = when (state) {
        PaymentState.Succeeded -> RideTrigger.PAYMENT_SUCCEEDED
        PaymentState.FellBackToCash -> RideTrigger.CASH_SETTLED
        PaymentState.DriverConfirmedQR -> RideTrigger.CASH_SETTLED
        PaymentState.CashOnDeliveryCollected -> RideTrigger.COD_COLLECTED
        PaymentState.Disputed -> RideTrigger.DISPUTE_RAISED
        else -> null
    }

    /**
     * The ride state a terminal payment settles into, or `null` when it settles nothing.
     *
     * Reads [settlementTrigger] through the ride's own table rather than repeating it, so the two
     * cannot drift.
     */
    public fun settledRideState(state: PaymentState): RideState? =
        settlementTrigger(state)?.let { RideTransitions.next(RideState.PaymentPending, it) }
}
