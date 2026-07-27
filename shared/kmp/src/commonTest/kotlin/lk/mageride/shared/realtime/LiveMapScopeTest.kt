package lk.mageride.shared.realtime

import lk.mageride.shared.domain.geo.COLOMBO_FORT
import lk.mageride.shared.domain.geo.GeoCells
import lk.mageride.shared.domain.geo.TestH3Grid
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

/** AL-31's fence, and D6' §5.4's recovery order. */
class LiveMapScopeTest {

    private val cells = GeoCells.viewCells(TestH3Grid(), COLOMBO_FORT)

    @Test
    fun a_passenger_joins_one_group_per_cell() {
        val scope = LiveMapScope.PassengerView(cells)

        assertEquals(19, scope.groups.size)
        assertTrue(scope.groups.all { it.startsWith("cell:") })
        assertEquals(cells.map(LiveHub::cellGroup).toSet(), scope.groups)
    }

    @Test
    fun the_driver_home_map_joins_no_geocell_group_at_all() {
        // AL-31: the driver home map is scoped to the driver's OWN active vehicle; other drivers'
        // active vehicles are never rendered on it. Expressed as the shape of the type, so there
        // is no plausible edit that puts a driver into a `cell:` group by accident.
        val scope: LiveMapScope = LiveMapScope.DriverHomeMap

        assertTrue(scope.groups.isEmpty())
        assertTrue(scope.groups.none { it.startsWith("cell:") })
    }

    @Test
    fun the_recovery_plan_rejoins_groups_before_it_asks_for_the_snapshot() {
        // The order is the contract: snapshot-then-join loses every frame published between the
        // two calls, and those are exactly the ones that moved while the client was away.
        val plan = LiveHubRecovery.plan(
            scope = LiveMapScope.PassengerView(cells),
            activeRides = setOf("R1"),
            pendingLocationRequests = setOf("Q1"),
        )

        assertIs<RecoveryStep.JoinGeocells>(plan.first())
        assertEquals(RecoveryStep.ResyncNearbySnapshot, plan.last())
        assertTrue(plan.contains(RecoveryStep.SubscribeRide("R1")))
        assertTrue(plan.contains(RecoveryStep.SubscribeLocationRequest("Q1")))
    }

    @Test
    fun a_driver_reconnecting_mid_ride_resubscribes_to_the_ride_and_nothing_else() {
        val plan = LiveHubRecovery.plan(LiveMapScope.DriverHomeMap, activeRides = setOf("R1"))

        assertEquals(listOf(RecoveryStep.SubscribeRide("R1")), plan)
    }

    @Test
    fun a_client_with_nothing_to_restore_sends_nothing() {
        assertTrue(LiveHubRecovery.plan(LiveMapScope.PassengerView(emptySet())).isEmpty())
        assertTrue(LiveHubRecovery.plan(LiveMapScope.DriverHomeMap).isEmpty())
    }
}
