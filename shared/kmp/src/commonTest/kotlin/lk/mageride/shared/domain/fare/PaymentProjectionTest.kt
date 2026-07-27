package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.fare.PaymentMethod
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * The payment projection and the AL-47 driver-QR attestation.
 *
 * Two rules carry the file: **the client never advances a payment**, and **a server move the table
 * does not draw is applied and flagged, never dropped**.
 */
class PaymentProjectionTest {

    private fun projection(state: PaymentState = PaymentState.Initiated, method: PaymentMethod = PaymentMethod.CASH) =
        PaymentProjection(PaymentSnapshot.of(paymentStatus(state, method)))

    @Test
    fun a_server_frame_moves_the_projection_and_names_the_edge() {
        val projection = projection(PaymentState.Pending, PaymentMethod.ONEPAY)

        val update = projection.onServerState(paymentStatus(PaymentState.Succeeded, PaymentMethod.ONEPAY))

        val applied = assertIs<PaymentUpdate.Applied>(update)
        assertEquals(PaymentState.Pending, applied.from)
        assertEquals(PaymentState.Succeeded, applied.to)
        assertEquals(PaymentTrigger.PROVIDER_SUCCEEDED, applied.trigger)
        assertTrue(applied.isKnownEdge)
        assertTrue(applied.releasesEarning)
        assertEquals(PaymentState.Succeeded, projection.state)
    }

    @Test
    fun an_edge_the_table_does_not_draw_is_applied_and_flagged() {
        // fare-svc is the sole writer. Refusing its answer would leave a passenger looking at a
        // settled ride that says it still owes money.
        val projection = projection(PaymentState.Pending)

        val update = projection.onServerState(paymentStatus(PaymentState.DriverConfirmedQR))

        val applied = assertIs<PaymentUpdate.Applied>(update)
        assertFalse(applied.isKnownEdge, "the table draws no Pending -> DriverConfirmedQR edge")
        assertNull(applied.trigger)
        assertEquals(PaymentState.DriverConfirmedQR, projection.state, "and the server still wins")
    }

    @Test
    fun a_duplicate_frame_changes_nothing() {
        val projection = projection(PaymentState.Pending)

        val update = projection.onServerState(paymentStatus(PaymentState.Pending))

        assertEquals(PaymentUpdate.Ignored(PaymentUpdateIgnored.DUPLICATE), update)
    }

    @Test
    fun a_settled_payment_is_never_walked_back() {
        // `PaymentStatus` carries no version, so this is the only ordering rule available: an
        // in-flight poll answering after the settling push must not un-settle the ride.
        val projection = projection(PaymentState.Pending)
        projection.onServerState(paymentStatus(PaymentState.Succeeded))

        val late = projection.onServerState(paymentStatus(PaymentState.Pending))

        assertEquals(PaymentUpdate.Ignored(PaymentUpdateIgnored.ALREADY_TERMINAL), late)
        assertEquals(PaymentState.Succeeded, projection.state)
    }

    @Test
    fun the_earning_is_released_only_by_a_settling_state() {
        // R-05, extended by AL-47. `Refunded` and `Disputed` are terminal without having paid
        // anybody, so `isTerminal` is the wrong question.
        val settling = listOf(
            PaymentState.Succeeded,
            PaymentState.FellBackToCash,
            PaymentState.CashOnDeliveryCollected,
            PaymentState.DriverConfirmedQR,
        )
        settling.forEach {
            assertTrue(projection(it).isEarningReleased, "$it releases the earning")
        }

        listOf(PaymentState.Refunded, PaymentState.Disputed, PaymentState.Pending, PaymentState.Overpaid)
            .forEach { assertFalse(projection(it).isEarningReleased, "$it does not") }
    }

    @Test
    fun a_local_verdict_reads_the_table_and_is_not_a_promise() {
        val projection = projection(PaymentState.Failed)

        assertTrue(projection.canSend(PaymentTrigger.PASSENGER_RETRIED))
        assertTrue(projection.canSend(PaymentTrigger.SETTLED_IN_CASH))
        assertFalse(projection.canSend(PaymentTrigger.PROVIDER_SUCCEEDED))
    }

    // ----------------------------------------------------------------------------------------
    // AL-47 — the +5 minute nudge and the escalation behind it
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_nudge_falls_due_five_minutes_after_the_claim() {
        val claimedAt = colombo(14)

        assertEquals(claimedAt + 5.minutes, DriverQrAttestation.nudgeDueAt(claimedAt))
        assertFalse(DriverQrAttestation.isNudgeDue(claimedAt, claimedAt + 4.minutes))
        assertTrue(DriverQrAttestation.isNudgeDue(claimedAt, claimedAt + 5.minutes))
        assertEquals(60.seconds, DriverQrAttestation.remainingBeforeNudge(claimedAt, claimedAt + 4.minutes))
        assertEquals(
            kotlin.time.Duration.ZERO,
            DriverQrAttestation.remainingBeforeNudge(claimedAt, claimedAt + 9.minutes),
        )
    }

    @Test
    fun a_claim_starts_the_countdown_and_a_later_poll_does_not_restart_it() {
        val projection = projection(PaymentState.Initiated, PaymentMethod.SCAN_DRIVER_QR)
        val claimedAt = colombo(14)

        projection.onServerState(
            paymentStatus(PaymentState.QrClaimedByPassenger, PaymentMethod.SCAN_DRIVER_QR),
            observedAt = claimedAt,
        )
        // A poll four minutes later reports the same state; the deadline must not move.
        val duplicate = projection.onServerState(
            paymentStatus(PaymentState.QrClaimedByPassenger, PaymentMethod.SCAN_DRIVER_QR),
            observedAt = claimedAt + 4.minutes,
        )

        assertEquals(PaymentUpdate.Ignored(PaymentUpdateIgnored.DUPLICATE), duplicate)
        assertEquals(claimedAt + 5.minutes, projection.nudgeDueAt())
    }

    @Test
    fun get_help_appears_only_once_the_nudge_has_gone_unanswered() {
        val projection = projection(PaymentState.Initiated, PaymentMethod.SCAN_DRIVER_QR)
        val claimedAt = colombo(14)
        projection.onServerState(
            paymentStatus(PaymentState.QrClaimedByPassenger, PaymentMethod.SCAN_DRIVER_QR),
            observedAt = claimedAt,
        )

        assertFalse(projection.escalationAvailable(claimedAt + 1.minutes), "the driver may not have looked yet")
        assertTrue(projection.escalationAvailable(claimedAt + 6.minutes))
    }

    @Test
    fun a_confirmed_payment_has_no_outstanding_claim_and_no_escalation() {
        val projection = projection(PaymentState.Initiated, PaymentMethod.SCAN_DRIVER_QR)
        val claimedAt = colombo(14)
        projection.onServerState(
            paymentStatus(PaymentState.QrClaimedByPassenger, PaymentMethod.SCAN_DRIVER_QR),
            observedAt = claimedAt,
        )

        val confirmed = projection.onServerState(
            paymentStatus(PaymentState.DriverConfirmedQR, PaymentMethod.SCAN_DRIVER_QR),
            observedAt = claimedAt + 2.minutes,
        )

        val applied = assertIs<PaymentUpdate.Applied>(confirmed)
        assertEquals(PaymentTrigger.QR_CONFIRMED_BY_DRIVER, applied.trigger)
        assertTrue(applied.releasesEarning, "AL-47: the earning posts on DriverConfirmedQR")
        assertNull(projection.nudgeDueAt())
        assertFalse(projection.escalationAvailable(claimedAt + 30.minutes))
    }

    @Test
    fun escalation_is_never_offered_on_a_payment_that_was_never_claimed() {
        val projection = projection(PaymentState.Pending, PaymentMethod.ONEPAY)

        assertFalse(projection.escalationAvailable(colombo(23)))
        assertNull(projection.nudgeDueAt())
    }

    // ----------------------------------------------------------------------------------------
    // The snapshot's own arithmetic
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_charged_total_is_fare_plus_surcharge_plus_tip() {
        val snapshot = PaymentSnapshot.of(
            paymentStatus(
                state = PaymentState.Succeeded,
                method = PaymentMethod.ONEPAY,
                amountMinor = 48_000,
                surchargeMinor = 2_400,
                tipMinor = 10_000,
            ),
        )

        assertEquals(48_000L, snapshot.amount.amountMinor)
        assertEquals(60_400L, snapshot.chargedTotal.amountMinor)
    }

    @Test
    fun a_frame_with_no_settlement_instant_does_not_erase_the_one_we_hold() {
        val settledAt = colombo(15)
        val projection = projection(PaymentState.Pending)
        projection.onServerState(paymentStatus(PaymentState.Succeeded).copy(settledAt = settledAt))

        projection.onServerState(paymentStatus(PaymentState.Succeeded))

        assertEquals(settledAt, projection.snapshot.value.settledAt)
    }
}
