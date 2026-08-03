package lk.mageride.driver.menu

import lk.mageride.driver.nav.DriverRoute
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * SCR-DA-036's drawer, as a table.
 *
 * D2' §SCR-DA-036 names **eight** destinations and AL-31 makes this list the whole replacement for
 * the hamburger, so both the count and the reachability are worth pinning: a drawer row pointing at
 * a route the NavHost does not register is a tap that does nothing, and it is exactly the kind of
 * thing that stays broken because nobody opens the menu.
 */
class MenuDestinationTest {

    @Test
    fun the_drawer_lists_the_eight_documented_destinations_in_order() {
        assertEquals(
            listOf(
                MenuDestination.MyVehicles,
                MenuDestination.VehicleOnboarding,
                MenuDestination.TrackerPairing,
                MenuDestination.Sharing,
                MenuDestination.Profile,
                MenuDestination.RideHistory,
                MenuDestination.Support,
                MenuDestination.Notifications,
            ),
            MenuDestination.entries.toList(),
            "the order driver_android.html prints them in",
        )
    }

    @Test
    fun every_row_goes_somewhere_the_nav_host_registers() {
        // C070 added the four routes SCR-DA-036 needed and the shell's table was missing rather
        // than pointing four rows at the nearest existing screen. Until C071–C074 land, each of
        // those opens the standing placeholder — which names the prompt that owns it.
        MenuDestination.entries.forEach { destination ->
            assertTrue(
                destination.route in DriverRoute.Static,
                "${destination.name} points at ${destination.route.path}, which the NavHost does not register",
            )
        }
    }

    @Test
    fun no_two_rows_open_the_same_screen() {
        val paths = MenuDestination.entries.map { it.route.path }
        assertEquals(paths.distinct(), paths, "two drawer rows lead to one screen")
    }
}
