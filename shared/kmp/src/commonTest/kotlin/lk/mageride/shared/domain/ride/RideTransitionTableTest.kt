package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.RideState
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * ADD Appendix B.2, re-declared here from the diagram and the D5' §6/§7 prose **without looking at
 * [RideTransitions]**.
 *
 * The point of writing it twice is that the two are then independent statements of the same thing,
 * and [the_table_is_exactly_appendix_b_2] fails if either drifts. Reading the production table into
 * the test would make every assertion below a tautology.
 */
private val APPENDIX_B2: Set<RideEdge> = setOf(
    // POST /rides/request → Requested → Matching
    RideEdge(RideState.Requested, RideTrigger.DISPATCH_STARTED, RideState.Matching),
    // "no candidates / round timeout → ExpiredNoDriver (terminal)"
    RideEdge(RideState.Matching, RideTrigger.NO_DRIVER_FOUND, RideState.ExpiredNoDriver),
    // "dispatch reserves driver, sends offer"
    RideEdge(RideState.Matching, RideTrigger.OFFER_PUSHED, RideState.Offered),
    // "decline / expire (15s) → re-enter Matching"
    RideEdge(RideState.Offered, RideTrigger.OFFER_DECLINED, RideState.Matching),
    RideEdge(RideState.Offered, RideTrigger.OFFER_EXPIRED, RideState.Matching),
    // "atomic accept (§11.11)"
    RideEdge(RideState.Offered, RideTrigger.OFFER_ACCEPTED, RideState.Accepted),
    // "driver enters pickup geofence" / "driver taps Start" / "driver taps Complete"
    RideEdge(RideState.Accepted, RideTrigger.DRIVER_ARRIVED, RideState.DriverArrived),
    RideEdge(RideState.DriverArrived, RideTrigger.RIDE_STARTED, RideState.InProgress),
    RideEdge(RideState.InProgress, RideTrigger.RIDE_COMPLETED, RideState.Completed),
    RideEdge(RideState.Completed, RideTrigger.FARE_FINALISED, RideState.PaymentPending),
    // "rider cancel → CancelledByRiderAfterAccept (terminal, Rs 50)" / "(terminal, full fare)"
    RideEdge(RideState.Accepted, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderAfterAccept),
    RideEdge(RideState.InProgress, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderAfterAccept),
    // "rider no-show 5min → NoShowRider (terminal, Rs 100)"
    RideEdge(RideState.DriverArrived, RideTrigger.RIDER_NO_SHOW, RideState.NoShowRider),
    // "Any pre-Accepted state + rider cancel → CancelledByRiderBeforeAccept (terminal, no penalty)"
    RideEdge(RideState.Requested, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderBeforeAccept),
    RideEdge(RideState.Matching, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderBeforeAccept),
    RideEdge(RideState.Offered, RideTrigger.RIDER_CANCELLED, RideState.CancelledByRiderBeforeAccept),
    // "Any post-Accepted state + driver cancel / LWT offline beyond grace → CancelledByDriver"
    RideEdge(RideState.Accepted, RideTrigger.DRIVER_CANCELLED, RideState.CancelledByDriver),
    RideEdge(RideState.DriverArrived, RideTrigger.DRIVER_CANCELLED, RideState.CancelledByDriver),
    RideEdge(RideState.InProgress, RideTrigger.DRIVER_CANCELLED, RideState.CancelledByDriver),
    // D5' §6.3's per-state grace, whose outcome is CancelledByDriver before the ride starts and
    // Disputed once a passenger is aboard (D5' §6 mermaid, §7 matrix).
    RideEdge(RideState.Accepted, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.CancelledByDriver),
    RideEdge(RideState.DriverArrived, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.CancelledByDriver),
    RideEdge(RideState.InProgress, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.Disputed),
    RideEdge(RideState.PaymentPending, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED, RideState.Disputed),
    // Settlement: "provider Succeeded" / "cash fallback" / P-08 / "dispute / overpaid"
    RideEdge(RideState.PaymentPending, RideTrigger.PAYMENT_SUCCEEDED, RideState.Paid),
    RideEdge(RideState.PaymentPending, RideTrigger.CASH_SETTLED, RideState.CashSettled),
    RideEdge(RideState.PaymentPending, RideTrigger.COD_COLLECTED, RideState.CashOnDeliveryCollected),
    RideEdge(RideState.PaymentPending, RideTrigger.DISPUTE_RAISED, RideState.Disputed),
)

/**
 * The two edges the Appendix B.2 **diagram** does not draw, each carried by a different part of the
 * same spec.
 *
 * They are listed apart rather than folded into [APPENDIX_B2] so that "the table is Appendix B.2
 * plus exactly these two, for exactly these reasons" is something the test states rather than
 * something a reader has to reconstruct. Both are recorded in the C015 handoff.
 */
private val SPEC_ADDITIONS: Set<RideEdge> = setOf(
    // D5' §6.1's accept guard: `WHERE ... state IN ('Matching','Offered')`.
    RideEdge(RideState.Matching, RideTrigger.OFFER_ACCEPTED, RideState.Accepted),
    // D5' §7 matrix: "Accepted/DriverArrived | driver never reaches pickup, grace exceeded |
    // NoShowDriver". The state is in the `ck_rides_state` CHECK (C004) and in `RideState` (C012).
    RideEdge(RideState.Accepted, RideTrigger.DRIVER_NO_SHOW, RideState.NoShowDriver),
    RideEdge(RideState.DriverArrived, RideTrigger.DRIVER_NO_SHOW, RideState.NoShowDriver),
)

/** Seeded, so a failing walk is a walk anyone can reproduce. */
private const val FUZZ_SEED = 20260727

private const val FUZZ_WALKS = 500
private const val FUZZ_STEPS = 40

/**
 * The Mode C ride machine's shape (ADD Appendix B.2, D5' §6, D5' §7).
 *
 * The DoD asks for property tests proving no transition outside Appendix B.2 is reachable. For a
 * machine with 18 states and 20 triggers the whole input space is 360 pairs, so these enumerate it
 * exhaustively rather than sampling it — a stronger statement than any number of random draws, and
 * a faster one.
 */
class RideTransitionTableTest {

    @Test
    fun the_table_is_exactly_appendix_b_2_plus_the_two_documented_spec_additions() {
        assertEquals(APPENDIX_B2 + SPEC_ADDITIONS, RideTransitions.EDGES)
    }

    @Test
    fun every_state_trigger_pair_outside_the_table_moves_nothing() {
        val declared = RideTransitions.EDGES.map { it.from to it.trigger }.toSet()
        var checked = 0

        for (state in RideState.entries) {
            for (trigger in RideTrigger.entries) {
                checked++
                if ((state to trigger) in declared) continue
                assertNull(
                    RideTransitions.next(state, trigger),
                    "$state + $trigger is not in Appendix B.2 but the table moved the ride",
                )
                assertTrue(!RideTransitions.isLegal(state, trigger), "$state + $trigger reported legal")
            }
        }

        assertEquals(RideState.entries.size * RideTrigger.entries.size, checked, "the sweep was not exhaustive")
    }

    @Test
    fun a_terminal_state_has_no_way_out() {
        for (state in RideState.entries.filter { it.isTerminal }) {
            assertEquals(emptySet(), RideTransitions.triggersFrom(state), "$state is terminal but has triggers")
        }
    }

    @Test
    fun every_non_terminal_state_has_a_way_out() {
        for (state in RideState.entries.filter { !it.isTerminal }) {
            assertTrue(RideTransitions.triggersFrom(state).isNotEmpty(), "$state is a dead end")
        }
    }

    @Test
    fun every_one_of_the_eighteen_states_is_reachable_from_requested() {
        val seen = mutableSetOf(RideState.Requested)
        val queue = ArrayDeque(listOf(RideState.Requested))
        while (queue.isNotEmpty()) {
            for (next in RideTransitions.successorsOf(queue.removeFirst())) {
                if (seen.add(next)) queue += next
            }
        }

        assertEquals(RideState.entries.toSet(), seen, "unreachable: ${RideState.entries.toSet() - seen}")
    }

    @Test
    fun a_trigger_never_lands_in_two_places_from_the_same_state() {
        val duplicated = RideTransitions.EDGES
            .groupBy { it.from to it.trigger }
            .filterValues { it.size > 1 }

        assertEquals(emptyMap(), duplicated, "a (from, trigger) pair must have one destination")
    }

    @Test
    fun a_random_walk_only_ever_visits_states_the_table_draws() {
        val random = Random(FUZZ_SEED)
        var moves = 0

        repeat(FUZZ_WALKS) {
            var state = RideState.Requested
            repeat(FUZZ_STEPS) {
                val trigger = RideTrigger.entries[random.nextInt(RideTrigger.entries.size)]
                val next = RideTransitions.next(state, trigger) ?: return@repeat

                assertContains(RideTransitions.EDGES, RideEdge(state, trigger, next))
                assertTrue(
                    !state.isTerminal,
                    "the walk moved out of terminal $state via $trigger",
                )
                state = next
                moves++
            }
            assertTrue(state.isTerminal || RideTransitions.triggersFrom(state).isNotEmpty())
        }

        assertTrue(moves > FUZZ_WALKS, "the walk barely moved ($moves moves) — the seed is not exercising it")
    }

    @Test
    fun every_command_maps_onto_an_edge_the_table_actually_draws() {
        for (command in RideCommand.entries) {
            val reachable = RideState.entries.filter { RideTransitions.isLegal(it, command.trigger) }
            assertTrue(reachable.isNotEmpty(), "${command.name} fires ${command.trigger}, which nothing can fire")
        }
    }

    @Test
    fun the_grace_windows_and_their_outcomes_agree_with_the_table() {
        // R-16's four windows, and no fifth: a state with a grace window but no expiry edge would
        // leave a ride sitting on a timer that could never fire.
        val graced = RideState.entries.filter { RideGrace.windowFor(it) != null }

        assertEquals(
            listOf(RideState.Accepted, RideState.DriverArrived, RideState.InProgress, RideState.PaymentPending),
            graced,
        )
        for (state in graced) {
            assertEquals(
                RideTransitions.next(state, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED),
                RideGrace.outcomeFor(state),
                "$state's grace outcome disagrees with Appendix B.2",
            )
        }
    }
}
