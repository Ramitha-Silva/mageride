package lk.mageride.shared.data.models

import kotlinx.serialization.Serializable

/**
 * The 18 states of the Mode C ride aggregate (D5' §6, ADD Appendix B.2,
 * `_shared.yaml#/components/schemas/RideState`).
 *
 * **This list is the `ck_rides_state` CHECK constraint, verbatim** (`0601__rides_rides.sql`,
 * C004) — same values, same spelling, same count. A state the CHECK rejects cannot be persisted,
 * so a client that invented one could only ever produce a `400` (C012 fence, DoD).
 *
 * ride-svc is the **sole writer** (R-01); dispatch-svc consumes ride events and emits offer
 * events but never writes `rides.rides`. The transition rules themselves belong to the state
 * machine in C015 — what lives here is only which states exist and which of them are terminal,
 * both of which are properties of the enum rather than of a transition.
 *
 * The aggregate is **kind-agnostic** (ADD Appendix B.2 invariant 6): `passenger`, `proxy` and
 * `package` rides traverse the same states. See [RideKind].
 *
 * > `NoShowDriver` is present for completeness (B0 GAP-G3 / backlog B4). D5' §7 models
 * > driver-side no-show as [CancelledByDriver] and **no transition currently writes it**; it is
 * > in the CHECK, so it is here.
 */
@Serializable
public enum class RideState {
    /** Rider has asked for a ride; the outbox row is written but dispatch has not seen it yet. */
    Requested,

    /** Dispatch is scoring candidates. Re-entered when a driver declines or an offer expires. */
    Matching,

    /** An offer is live with one driver, under a 15-second TTL (R-02, §11.11). */
    Offered,

    /** A driver won the atomic accept. The Rs 50 cross-trip cancellation penalty starts here. */
    Accepted,

    /** The driver entered the pickup geofence, or tapped Arrived. */
    DriverArrived,

    /** Rider on board (or package picked up); the fare meter is running. */
    InProgress,

    /** The driver tapped Complete; fare-svc is about to compute the final fare. */
    Completed,

    /** Awaiting settlement. The driver's earning does not post until a terminal money state. */
    PaymentPending,

    /** Terminal: settled digitally through a gateway. */
    Paid,

    /** Terminal: settled in cash, including the fallback from a failed digital payment. */
    CashSettled,

    /** Terminal: package cash-on-delivery collected by the driver (P-08). */
    CashOnDeliveryCollected,

    /** Terminal-with-followup: a post-payment dispute is open; refunds are Finance-only (E-05). */
    Disputed,

    /** Terminal: rider cancelled before any driver accepted. No penalty. */
    CancelledByRiderBeforeAccept,

    /** Terminal: rider cancelled after acceptance. Rs 50 accrues, settled on the next trip. */
    CancelledByRiderAfterAccept,

    /** Terminal: driver cancelled, or went offline beyond the grace window (R-15). */
    CancelledByDriver,

    /** Terminal: no candidate accepted within the dispatch rounds. */
    ExpiredNoDriver,

    /** Terminal: rider was not at the pickup point five minutes after arrival. Rs 100. */
    NoShowRider,

    /** Terminal, reserved: see the note on this enum — nothing writes it today. */
    NoShowDriver,
    ;

    /**
     * Whether the aggregate can still move.
     *
     * [Disputed] counts as terminal — the ride is over and a refund is a separate Finance
     * workflow, not a further ride transition (E-05).
     */
    public val isTerminal: Boolean get() = this in TERMINAL

    /** Whether the ride has been assigned to a driver, i.e. a counterparty exists (AL-48). */
    public val isDriverAssigned: Boolean get() = this in DRIVER_ASSIGNED

    public companion object {
        private val TERMINAL: Set<RideState> = setOf(
            Paid,
            CashSettled,
            CashOnDeliveryCollected,
            Disputed,
            CancelledByRiderBeforeAccept,
            CancelledByRiderAfterAccept,
            CancelledByDriver,
            ExpiredNoDriver,
            NoShowRider,
            NoShowDriver,
        )

        private val DRIVER_ASSIGNED: Set<RideState> = setOf(
            Accepted,
            DriverArrived,
            InProgress,
            Completed,
            PaymentPending,
            Paid,
            CashSettled,
            CashOnDeliveryCollected,
            Disputed,
        )

        /**
         * The states a driver may hold at most one ride in
         * (`ux_rides_open_driver`, ADD Appendix B.2 invariant 2).
         */
        public val DRIVER_EXCLUSIVE: Set<RideState> =
            setOf(Accepted, DriverArrived, InProgress, PaymentPending)
    }
}
