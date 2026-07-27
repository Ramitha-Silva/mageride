package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.domain.ride.RideTrigger
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The payment machine, re-declared from the specs and swept exhaustively.
 *
 * The table in [PaymentTransitions] is written from D5' §8.1's mermaid plus §8.2, §8.3, AL-47 and
 * R-19; this test writes it out again from the same sources, independently, and asserts the two
 * agree edge for edge. Then it sweeps **every** `(state, trigger)` pair — 14 states × 14 triggers
 * — so an edge that crept in without being declared here fails the build rather than a screen.
 */
class PaymentTransitionTableTest {

    /**
     * D5' §8.1's mermaid, transcribed.
     *
     * ```
     * Initiated --> Pending: OnePay/LankaQR provider
     * Pending --> Succeeded / Failed
     * Failed --> Retried / FellBackToCash
     * Pending --> CashOnDelivery
     * CashOnDelivery --> CashOnDeliveryCollected
     * FellBackToCash --> Overpaid --> Refunded
     * ```
     */
    private val fromTheDiagram = setOf(
        PaymentEdge(PaymentState.Initiated, PaymentTrigger.GATEWAY_HANDOFF, PaymentState.Pending),
        PaymentEdge(PaymentState.Pending, PaymentTrigger.PROVIDER_SUCCEEDED, PaymentState.Succeeded),
        PaymentEdge(PaymentState.Pending, PaymentTrigger.PROVIDER_FAILED, PaymentState.Failed),
        PaymentEdge(PaymentState.Failed, PaymentTrigger.PASSENGER_RETRIED, PaymentState.Retried),
        PaymentEdge(PaymentState.Failed, PaymentTrigger.SETTLED_IN_CASH, PaymentState.FellBackToCash),
        PaymentEdge(PaymentState.Pending, PaymentTrigger.COD_SELECTED, PaymentState.CashOnDelivery),
        PaymentEdge(
            PaymentState.CashOnDelivery,
            PaymentTrigger.COD_COLLECTED,
            PaymentState.CashOnDeliveryCollected,
        ),
        PaymentEdge(PaymentState.FellBackToCash, PaymentTrigger.LATE_PROVIDER_CALLBACK, PaymentState.Overpaid),
        PaymentEdge(PaymentState.Overpaid, PaymentTrigger.REFUND_COMPLETED, PaymentState.Refunded),
    )

    /**
     * The edges the diagram does not draw, each carried by other prose in the same specs.
     *
     * Listed separately so the claim "these four groups are extensions, and here is why" stays
     * auditable rather than being buried in the union.
     */
    private val fromTheProse = setOf(
        // §8.1 methods: "Cash (default, driver collects)" — no gateway leg to fail first.
        PaymentEdge(PaymentState.Initiated, PaymentTrigger.SETTLED_IN_CASH, PaymentState.FellBackToCash),

        // §8.3: "Package COD: CashOnDelivery set at delivery" — likewise no gateway leg.
        PaymentEdge(PaymentState.Initiated, PaymentTrigger.COD_SELECTED, PaymentState.CashOnDelivery),

        // §8.3 / P-14: the 24 h `cod_uncollected` timer.
        PaymentEdge(PaymentState.CashOnDelivery, PaymentTrigger.COD_UNCOLLECTED_TIMEOUT, PaymentState.Disputed),

        // §8.2 / E-05: Finance's admin-initiated full and partial reversals, and a rider dispute.
        PaymentEdge(PaymentState.Succeeded, PaymentTrigger.REFUND_COMPLETED, PaymentState.Refunded),
        PaymentEdge(
            PaymentState.Succeeded,
            PaymentTrigger.PARTIAL_REFUND_COMPLETED,
            PaymentState.PartiallyRefunded,
        ),
        PaymentEdge(PaymentState.Succeeded, PaymentTrigger.DISPUTE_RAISED, PaymentState.Disputed),

        // AL-47 / BR-30.1, the driver-QR attestation pair.
        PaymentEdge(
            PaymentState.Initiated,
            PaymentTrigger.QR_CLAIMED_BY_PASSENGER,
            PaymentState.QrClaimedByPassenger,
        ),
        PaymentEdge(
            PaymentState.QrClaimedByPassenger,
            PaymentTrigger.QR_CONFIRMED_BY_DRIVER,
            PaymentState.DriverConfirmedQR,
        ),
        PaymentEdge(PaymentState.Initiated, PaymentTrigger.QR_CONFIRMED_BY_DRIVER, PaymentState.DriverConfirmedQR),
        PaymentEdge(PaymentState.QrClaimedByPassenger, PaymentTrigger.DISPUTE_RAISED, PaymentState.Disputed),
        PaymentEdge(
            PaymentState.QrClaimedByPassenger,
            PaymentTrigger.SETTLED_IN_CASH,
            PaymentState.FellBackToCash,
        ),
    )

    @Test
    fun the_table_is_exactly_the_specs_edges() {
        assertEquals(fromTheDiagram + fromTheProse, PaymentTransitions.EDGES)
    }

    @Test
    fun every_edge_the_diagram_draws_is_present() {
        fromTheDiagram.forEach {
            assertEquals(it.to, PaymentTransitions.next(it.from, it.trigger), "§8.1 draws $it")
        }
    }

    @Test
    fun no_pair_outside_the_table_moves_anything() {
        // The whole input space: 14 x 14 = 196 pairs. Enumerating it is a stronger statement than
        // sampling it, and it is faster.
        var checked = 0
        PaymentState.entries.forEach { from ->
            PaymentTrigger.entries.forEach { trigger ->
                checked++
                val declared = PaymentTransitions.EDGES.singleOrNull { it.from == from && it.trigger == trigger }
                assertEquals(declared?.to, PaymentTransitions.next(from, trigger), "$from / $trigger")
            }
        }
        assertEquals(PaymentState.entries.size * PaymentTrigger.entries.size, checked)
    }

    @Test
    fun a_retried_payment_has_no_outgoing_edge() {
        // US-8.15: a retry is a NEW row chained by `retry_of_payment_id`. This row is finished, and
        // the machine continues on its successor.
        assertTrue(PaymentTransitions.triggersFrom(PaymentState.Retried).isEmpty())
    }

    @Test
    fun a_driver_can_confirm_a_qr_payment_with_no_prior_claim() {
        // BR-30.1 states it explicitly, and it is the case that matters: the driver's bank app is
        // the only party that actually saw the money.
        assertEquals(
            PaymentState.DriverConfirmedQR,
            PaymentTransitions.next(PaymentState.Initiated, PaymentTrigger.QR_CONFIRMED_BY_DRIVER),
        )
        assertEquals(
            PaymentState.DriverConfirmedQR,
            PaymentTransitions.next(PaymentState.QrClaimedByPassenger, PaymentTrigger.QR_CONFIRMED_BY_DRIVER),
        )
    }

    @Test
    fun a_gateway_success_is_not_reachable_from_a_qr_claim() {
        // AL-47: "Gateway-verified `Succeeded` is OnePay-only". A driver-QR payment moves
        // bank-to-bank and produces no webhook, so no provider callback can settle one.
        assertNull(PaymentTransitions.next(PaymentState.QrClaimedByPassenger, PaymentTrigger.PROVIDER_SUCCEEDED))
        assertTrue(!PaymentTransitions.isReachable(PaymentState.QrClaimedByPassenger, PaymentState.Succeeded))
    }

    // ----------------------------------------------------------------------------------------
    // R-05 — which terminal states release the driver's earning, and into which ride state
    // ----------------------------------------------------------------------------------------

    @Test
    fun exactly_the_settling_states_map_to_a_ride_trigger() {
        val settling = PaymentState.entries.filter { PaymentTransitions.settlementTrigger(it) != null }

        assertEquals(
            setOf(
                PaymentState.Succeeded,
                PaymentState.FellBackToCash,
                PaymentState.DriverConfirmedQR,
                PaymentState.CashOnDeliveryCollected,
                PaymentState.Disputed,
            ),
            settling.toSet(),
        )
    }

    @Test
    fun a_settled_payment_names_the_ride_state_it_produces() {
        // R-05: "ride Completed ⇒ PaymentPending until settlement". The payment-side and ride-side
        // vocabularies differ — `Succeeded` is the payment, `Paid` is the ride — and this is the
        // only place the two are joined.
        assertEquals(RideState.Paid, PaymentTransitions.settledRideState(PaymentState.Succeeded))
        assertEquals(RideState.CashSettled, PaymentTransitions.settledRideState(PaymentState.FellBackToCash))
        assertEquals(
            RideState.CashOnDeliveryCollected,
            PaymentTransitions.settledRideState(PaymentState.CashOnDeliveryCollected),
        )
        assertEquals(RideState.Disputed, PaymentTransitions.settledRideState(PaymentState.Disputed))

        // AL-47: driver-QR "settles like cash". No spec names the resulting ride state outright.
        assertEquals(RideState.CashSettled, PaymentTransitions.settledRideState(PaymentState.DriverConfirmedQR))
        assertEquals(
            RideTrigger.CASH_SETTLED,
            PaymentTransitions.settlementTrigger(PaymentState.DriverConfirmedQR),
        )
    }

    @Test
    fun a_refund_settles_nothing() {
        assertNull(PaymentTransitions.settlementTrigger(PaymentState.Refunded))
        assertNull(PaymentTransitions.settlementTrigger(PaymentState.PartiallyRefunded))
        assertNull(PaymentTransitions.settlementTrigger(PaymentState.Overpaid))
        assertNull(PaymentTransitions.settlementTrigger(PaymentState.Pending))
    }

    @Test
    fun the_trigger_between_two_states_is_null_when_the_table_draws_more_than_one() {
        // `Initiated -> FellBackToCash` has one edge; `Failed -> FellBackToCash` also has one. But
        // `QrClaimedByPassenger -> Disputed` and `CashOnDelivery -> Disputed` are different edges,
        // and a bare status frame does not say which reason applied.
        assertEquals(
            PaymentTrigger.SETTLED_IN_CASH,
            PaymentTransitions.triggerBetween(PaymentState.Failed, PaymentState.FellBackToCash),
        )
        assertNull(PaymentTransitions.triggerBetween(PaymentState.Pending, PaymentState.Disputed))
    }
}
