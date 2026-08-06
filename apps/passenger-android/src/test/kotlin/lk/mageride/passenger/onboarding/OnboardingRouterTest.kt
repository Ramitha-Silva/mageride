package lk.mageride.passenger.onboarding

import lk.mageride.passenger.nav.PassengerRoute
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * The first-run gate — the one function that decides what a passenger sees on every cold start.
 *
 * It is pure precisely so this can exist: three screens finish by asking it where to go next, the
 * splash asks it on boot, and none of those agreeing is worth discovering on a handset.
 */
class OnboardingRouterTest {

    @Test
    fun a_brand_new_install_starts_at_the_language_carousel() {
        // AL-26: the language question comes FIRST, before login. A passenger who has not chosen
        // one would otherwise meet the login screen in whatever locale the handset is set to,
        // which for most users here is not one of the three.
        assertEquals(
            PassengerDestination.ONBOARDING,
            OnboardingRouter.next(
                signedIn = false,
                firstRunComplete = false,
                profileComplete = false,
                locationAcknowledged = false,
            ),
        )
    }

    @Test
    fun the_language_question_outranks_even_a_restored_session() {
        // A session can be restored on an install whose preferences were cleared — app data wiped,
        // or a restore from backup. The order is not "whatever is missing", it is fixed.
        assertEquals(
            PassengerDestination.ONBOARDING,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = false,
                profileComplete = true,
                locationAcknowledged = true,
            ),
        )
    }

    @Test
    fun the_four_gates_are_answered_in_order() {
        val gates = listOf(
            PassengerDestination.LOGIN to
                OnboardingRouter.next(false, firstRunComplete = true, false, false),
            PassengerDestination.PROFILE_SETUP to
                OnboardingRouter.next(true, firstRunComplete = true, profileComplete = false, false),
            PassengerDestination.LOCATION_PERMISSION to
                OnboardingRouter.next(true, true, profileComplete = true, locationAcknowledged = false),
            PassengerDestination.LIVE_MAP to
                OnboardingRouter.next(true, true, true, locationAcknowledged = true),
        )

        gates.forEach { (expected, actual) -> assertEquals(expected, actual) }
    }

    @Test
    fun a_denied_location_grant_still_reaches_the_map() {
        // `locationAcknowledged` is "SCR-PA-005 has been SHOWN", never "the grant was given" — the
        // grant belongs to the OS and can be revoked from Settings at any moment. Gating the map on
        // it would trap a passenger who said no, because Android stops showing the system dialog
        // after two refusals.
        assertEquals(
            PassengerDestination.LIVE_MAP,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = true,
                profileComplete = true,
                locationAcknowledged = true,
            ),
        )
    }

    @Test
    fun every_destination_names_a_route_the_shell_registers() {
        // A destination whose route is not in the graph is a splash that navigates into nothing.
        val registered = PassengerRoute.Static.map(PassengerRoute::path).toSet()

        PassengerDestination.entries.forEach { destination ->
            assert(destination.route.path in registered) {
                "${destination.name} -> ${destination.route.path} is not a registered destination"
            }
        }
    }

    @Test
    fun the_five_destinations_are_the_five_cluster_one_screens() {
        assertEquals(
            listOf(
                PassengerRoute.Onboarding,
                PassengerRoute.Login,
                PassengerRoute.ProfileSetup,
                PassengerRoute.LocationPermission,
                PassengerRoute.LiveMap,
            ),
            PassengerDestination.entries.map(PassengerDestination::route),
        )
    }
}
