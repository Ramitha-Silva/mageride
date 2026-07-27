package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.RideState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * D5' §7 — the cancellation & no-show matrix, and AL-16's cross-trip Rs 50.
 *
 * Nothing here charges anything: ride-svc is the sole author of a penalty (R-03) and the debt is
 * settled by fare-svc on the passenger's next completed trip (§7.1). What the matrix buys is the
 * confirmation sheet — a passenger who is about to lose Rs 50, or about to lose the ability to
 * book, should be told before the tap and not after it.
 */
class CancellationMatrixTest {

    @Test
    fun cancelling_before_anyone_accepts_is_free() {
        for (state in listOf(RideState.Requested, RideState.Matching, RideState.Offered)) {
            val outcome = CancellationMatrix.outcomeOf(state, RideTrigger.RIDER_CANCELLED)

            assertEquals(RideState.CancelledByRiderBeforeAccept, outcome?.to)
            assertEquals(CancellationCost.None, outcome?.cost, "US-6A.9: no penalty before acceptance")
            assertFalse(outcome!!.countsTowardBookingDisable, "§7.2: pre-acceptance cancels never count")
        }
    }

    @Test
    fun cancelling_after_acceptance_costs_fifty_rupees_and_counts() {
        val outcome = CancellationMatrix.outcomeOf(RideState.Accepted, RideTrigger.RIDER_CANCELLED)!!

        assertEquals(RideState.CancelledByRiderAfterAccept, outcome.to)
        assertEquals(CancellationCost.Flat(Money.ofMinor(5_000)), outcome.cost)
        assertTrue(outcome.countsTowardBookingDisable)
        assertTrue(outcome.releasesDriver)
    }

    @Test
    fun cancelling_mid_trip_costs_the_whole_fare_and_names_no_amount() {
        val outcome = CancellationMatrix.outcomeOf(RideState.InProgress, RideTrigger.RIDER_CANCELLED)!!

        // The meter is still running; fare-svc computes the figure from the filtered distance.
        assertIs<CancellationCost.FullFare>(outcome.cost)
        assertTrue(outcome.countsTowardBookingDisable)
    }

    @Test
    fun a_rider_no_show_costs_a_hundred_rupees_but_is_not_a_cancellation() {
        val outcome = CancellationMatrix.outcomeOf(RideState.DriverArrived, RideTrigger.RIDER_NO_SHOW)!!

        assertEquals(RideState.NoShowRider, outcome.to)
        assertEquals(CancellationCost.Flat(Money.ofMinor(10_000)), outcome.cost)
        // §7.2 counts post-acceptance *cancels*. Not turning up is a different row, and one that
        // already costs twice as much.
        assertFalse(outcome.countsTowardBookingDisable)
    }

    @Test
    fun a_driver_cancel_costs_the_driver_reputation_and_the_rider_nothing() {
        for (state in listOf(RideState.Accepted, RideState.DriverArrived, RideState.InProgress)) {
            val outcome = CancellationMatrix.outcomeOf(state, RideTrigger.DRIVER_CANCELLED)!!

            assertEquals(RideState.CancelledByDriver, outcome.to)
            assertEquals(CancellationCost.None, outcome.cost)
            assertTrue(outcome.driverReputationHit)
            assertFalse(outcome.countsTowardBookingDisable, "a driver cancel must never block the passenger")
        }
    }

    @Test
    fun an_offer_declined_or_expired_penalises_nobody_and_releases_the_driver() {
        for (trigger in listOf(RideTrigger.OFFER_DECLINED, RideTrigger.OFFER_EXPIRED)) {
            val outcome = CancellationMatrix.outcomeOf(RideState.Offered, trigger)!!

            assertEquals(RideState.Matching, outcome.to)
            assertEquals(CancellationCost.None, outcome.cost)
            assertTrue(outcome.releasesDriver)
        }
    }

    @Test
    fun every_row_lands_where_appendix_b_2_says_it_does() {
        // outcomeOf() checks this itself and throws on a mismatch; sweeping the whole space is what
        // makes that check run against every row rather than the handful a test happens to name.
        for (state in RideState.entries) {
            for (trigger in RideTrigger.entries) {
                val outcome = CancellationMatrix.outcomeOf(state, trigger) ?: continue
                assertEquals(RideTransitions.next(state, trigger), outcome.to)
            }
        }
    }

    @Test
    fun a_move_that_is_not_a_cancellation_has_no_row() {
        assertNull(CancellationMatrix.outcomeOf(RideState.InProgress, RideTrigger.RIDE_COMPLETED))
        assertNull(CancellationMatrix.outcomeOf(RideState.PaymentPending, RideTrigger.PAYMENT_SUCCEEDED))
    }

    @Test
    fun three_consecutive_post_acceptance_cancels_disable_booking() {
        var standing = PassengerStanding()
        val cancel = CancellationMatrix.outcomeOf(RideState.Accepted, RideTrigger.RIDER_CANCELLED)!!

        assertEquals(3, standing.cancelsBeforeBookingDisabled)

        standing = standing.afterCancellation(cancel)
        standing = standing.afterCancellation(cancel)

        assertFalse(standing.isBookingDisabled, "two is a warning, not a block")
        assertEquals(1, standing.cancelsBeforeBookingDisabled)
        assertEquals(Money.ofMinor(10_000), standing.outstandingBalance)

        standing = standing.afterCancellation(cancel)

        assertTrue(standing.isBookingDisabled)
        assertEquals(0, standing.cancelsBeforeBookingDisabled)
        assertEquals(Money.ofMinor(15_000), standing.outstandingBalance)
    }

    @Test
    fun pre_acceptance_cancels_never_move_the_counter_or_the_balance() {
        var standing = PassengerStanding()
        val free = CancellationMatrix.outcomeOf(RideState.Matching, RideTrigger.RIDER_CANCELLED)!!

        repeat(10) { standing = standing.afterCancellation(free) }

        assertEquals(PassengerStanding(), standing)
        assertFalse(standing.isBookingDisabled)
    }

    @Test
    fun a_completed_ride_settles_every_outstanding_penalty_and_resets_the_counter() {
        val cancel = CancellationMatrix.outcomeOf(RideState.Accepted, RideTrigger.RIDER_CANCELLED)!!
        val owing = PassengerStanding().afterCancellation(cancel).afterCancellation(cancel)

        assertEquals(Money.ofMinor(10_000), owing.outstandingBalance)

        // §7.1 loops over every OUTSTANDING penalty on the next completed trip, so a passenger who
        // owes Rs 100 pays Rs 100 — not Rs 50 with the rest carried on again.
        val settled = owing.afterCompletedRide()

        assertEquals(Money.ZERO, settled.outstandingBalance)
        assertFalse(settled.hasOutstandingBalance)
        assertEquals(0, settled.consecutivePostAcceptanceCancels)
    }

    @Test
    fun the_server_block_state_wins_over_the_local_count_in_both_directions() {
        val cancel = CancellationMatrix.outcomeOf(RideState.Accepted, RideTrigger.RIDER_CANCELLED)!!
        var standing = PassengerStanding(serverBookingDisabled = false)
        repeat(CancellationMatrix.CONSECUTIVE_CANCEL_LIMIT) { standing = standing.afterCancellation(cancel) }

        // Re-enablement needs the balance cleared *and* a cooldown or a CSR reinstatement (§7.2).
        // Neither is something a device can work out, so reputation-svc's answer is the answer.
        assertFalse(standing.isBookingDisabled)
        assertTrue(PassengerStanding(serverBookingDisabled = true).isBookingDisabled)
    }

    @Test
    fun the_cost_of_cancelling_right_now_is_what_the_sheet_shows() {
        assertEquals(CancellationCost.None, CancellationMatrix.costOfRiderCancelling(RideState.Matching))
        assertEquals(
            CancellationCost.Flat(CancellationMatrix.AFTER_ACCEPTANCE_PENALTY),
            CancellationMatrix.costOfRiderCancelling(RideState.Accepted),
        )
        // The rider cannot cancel while the driver waits at the kerb — D5' §7 has no such row and
        // Appendix B.2 draws no such edge. See the C015 handoff.
        assertNull(CancellationMatrix.costOfRiderCancelling(RideState.DriverArrived))
    }
}
