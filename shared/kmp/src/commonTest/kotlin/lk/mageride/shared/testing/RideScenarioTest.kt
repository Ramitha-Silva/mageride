package lk.mageride.shared.testing

import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.domain.ride.RideCommand
import lk.mageride.shared.domain.ride.RideTransitions
import lk.mageride.shared.domain.ride.RideUpdate
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.testing.scenario.ModeCRide
import lk.mageride.shared.testing.scenario.PackageDelivery
import lk.mageride.shared.testing.scenario.ProxyRide
import lk.mageride.shared.testing.scenario.RideScenarios
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C019 definition of done: *"the canonical Mode C ride scenario drives the state machine from
 * Requested to a terminal state in a test."*
 *
 * That is the first test below, and the rest are what stop it from being a happy accident: every
 * journey's edges are checked against ADD Appendix B.2 rather than against themselves, and the
 * three bookings the platform supports all walk the same kind-agnostic aggregate (invariant 6).
 */
class RideScenarioTest {

    @Test
    fun the_canonical_mode_c_ride_runs_from_requested_to_a_terminal_state() {
        val projection = ModeCRide.projection()
        assertEquals(RideState.Requested, projection.state)

        val updates = ModeCRide.drive(projection)

        assertEquals(
            listOf(
                RideState.Matching,
                RideState.Offered,
                RideState.Accepted,
                RideState.DriverArrived,
                RideState.InProgress,
                RideState.Completed,
                RideState.PaymentPending,
                RideState.Paid,
            ),
            updates.map { assertIs<RideUpdate.Applied>(it).to },
        )
        assertEquals(RideState.Paid, projection.state)
        assertTrue(projection.snapshot.value.isTerminal)
    }

    @Test
    fun every_move_is_an_edge_this_build_understands() {
        RideScenarios.forEach { scenario ->
            val updates = scenario.drive(scenario.projection())
            updates.forEach { update ->
                val applied = assertIs<RideUpdate.Applied>(update, scenario.name)
                assertTrue(applied.isKnownEdge, "${scenario.name}: ${applied.from} → ${applied.to}")
            }
        }
    }

    @Test
    fun every_journey_walks_the_appendix_b2_table_and_ends_terminal() {
        RideScenarios.forEach { scenario ->
            assertTrue(scenario.isWellFormed(), "${scenario.name}: ${scenario.edges()}")
            assertTrue(scenario.terminalState.isTerminal, scenario.name)
            scenario.edges().forEach { (from, trigger, to) ->
                assertEquals(to, RideTransitions.next(from, trigger), "${scenario.name}: $from -$trigger->")
            }
        }
    }

    @Test
    fun the_projection_names_the_trigger_the_server_took() {
        val scenario = ModeCRide
        val projection = scenario.projection()
        val updates = scenario.drive(projection)

        assertEquals(
            scenario.steps.map { it.trigger },
            updates.map { assertIs<RideUpdate.Applied>(it).trigger },
            "every edge in the canonical journey is the only one between its two states, so the " +
                "projection can name it rather than guess",
        )
    }

    @Test
    fun the_versions_increase_so_a_replayed_frame_is_ignored() {
        val scenario = ModeCRide
        val projection = scenario.projection()
        scenario.drive(projection)

        val stale = scenario.steps[2]
        val update = projection.onServerState(stale.state, stale.version)

        assertIs<RideUpdate.Ignored>(update)
        assertEquals(RideState.Paid, projection.state, "a late frame must not walk a settled ride back")
    }

    @Test
    fun the_three_bookings_traverse_the_same_states() {
        val paths = RideScenarios.map { scenario -> scenario.steps.dropLast(1).map { it.state } }
        assertEquals(
            1,
            paths.distinct().size,
            "ADD Appendix B.2 invariant 6: the aggregate is kind-agnostic, so only the settlement " +
                "state differs between a passenger ride, a proxy ride and a package. Got $paths",
        )
        assertEquals(
            listOf(RideState.Paid, RideState.CashSettled, RideState.CashOnDeliveryCollected),
            RideScenarios.map { it.terminalState },
        )
    }

    @Test
    fun a_proxy_ride_shows_the_driver_the_riders_number_and_not_the_bookers() {
        val accepted = ProxyRide.detail(ProxyRide.steps.first { it.state == RideState.Accepted })

        assertEquals(Fixtures.PASSENGER_ID, accepted.bookerId)
        assertNull(accepted.riderId, "P-01: the rider need not have an account")
        assertEquals("K. Silva", accepted.riderName)
        assertNotNull(accepted.counterpartyPhone, "AL-48: a counterparty exists from Accepted onward")
    }

    @Test
    fun a_counterparty_appears_only_once_a_driver_is_assigned() {
        val beforeAccept = ModeCRide.steps.filterNot { it.state.isDriverAssigned }
        assertTrue(beforeAccept.isNotEmpty())
        beforeAccept.forEach { step ->
            val detail = ModeCRide.detail(step)
            assertNull(detail.driver, "${step.state} must carry no driver")
            assertNull(detail.counterpartyPhone, "AL-48: ${step.state} must carry no phone")
        }
    }

    @Test
    fun a_package_ride_cannot_be_completed_until_both_handoffs_are_done() {
        val projection = PackageDelivery.projection()
        val handoff = assertNotNull(projection.handoff, "a package ride owns its handoff gates")

        PackageDelivery.drive(projection)

        assertFalse(handoff.state.value.canComplete, "AL-33: neither OTP has been presented")
        assertFalse(
            projection.canSend(RideCommand.COMPLETE),
            "the ride is terminal by now, which is a second and independent reason to refuse",
        )
    }

    @Test
    fun a_scenario_installed_on_the_fake_reproduces_itself_over_http() = runTest {
        val backend = ModeCRide.install(FakeApiBackend())
        val api = backend.mageRideApi()

        val booked = api.ride.requestRide(ModeCRide.request)
        val projection = ModeCRide.projection()
        assertEquals(RideState.Requested, booked.state)

        // The first poll re-reports the booking; the eight after it walk the journey.
        repeat(ModeCRide.steps.size + 1) { projection.onServerState(api.ride.getRideState(Fixtures.RIDE_ID)) }

        assertEquals(RideState.Paid, projection.state)
        assertEquals(RideState.Paid, api.ride.getRide(Fixtures.RIDE_ID).state)
    }
}
