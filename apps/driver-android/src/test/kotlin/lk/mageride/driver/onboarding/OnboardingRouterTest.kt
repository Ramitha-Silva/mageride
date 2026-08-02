package lk.mageride.driver.onboarding

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * SCR-DA-001's whole job, asserted as a table.
 *
 * The boot router decides what a driver sees on **every** cold start, and it has to agree with
 * what each of the four screens after it does when it finishes. Three of these cases are
 * indistinguishable on a handset until the wrong one happens to somebody.
 */
class OnboardingRouterTest {

    @Test
    fun a_first_run_starts_at_language_and_city_whatever_else_is_true() {
        // "First run only" (D2' §B SCR-DA-002) is checked before the session, deliberately: a
        // driver who has not chosen a language would otherwise meet the login screen in whatever
        // locale the handset is set to, which for most drivers here is not one of the three.
        assertEquals(
            OnboardingDestination.LANGUAGE_CITY,
            OnboardingRouter.next(
                signedIn = false,
                firstRunComplete = false,
                profileComplete = false,
                permissionsAcknowledged = false,
            ),
        )
        assertEquals(
            OnboardingDestination.LANGUAGE_CITY,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = false,
                profileComplete = true,
                permissionsAcknowledged = true,
            ),
            "an upgrade that arrives signed-in still has to choose a city (US-1.3a)",
        )
    }

    @Test
    fun no_session_goes_to_login() {
        assertEquals(
            OnboardingDestination.LOGIN,
            OnboardingRouter.next(
                signedIn = false,
                firstRunComplete = true,
                profileComplete = false,
                permissionsAcknowledged = false,
            ),
        )
    }

    @Test
    fun a_signed_in_driver_with_no_profile_goes_to_profile_setup() {
        // Change 6/22 dissolved the wireframe's "registered/not approved → RegistrationHub" into
        // this: driver identity, before Home, with no vehicle (AL-27, US-2.21).
        assertEquals(
            OnboardingDestination.PROFILE_SETUP,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = true,
                profileComplete = false,
                permissionsAcknowledged = true,
            ),
        )
    }

    @Test
    fun permissions_come_after_the_profile_and_before_home() {
        assertEquals(
            OnboardingDestination.PERMISSIONS,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = true,
                profileComplete = true,
                permissionsAcknowledged = false,
            ),
        )
    }

    @Test
    fun a_driver_with_a_profile_and_no_vehicle_reaches_home() {
        // AL-27's headline, and the DoD line this test exists for: "a driver reaches Home after
        // Profile Setup with no vehicle registered". Nothing in the router's inputs mentions a
        // vehicle, which is the point — there is no gate to forget to remove.
        assertEquals(
            OnboardingDestination.HOME,
            OnboardingRouter.next(
                signedIn = true,
                firstRunComplete = true,
                profileComplete = true,
                permissionsAcknowledged = true,
            ),
        )
    }

    @Test
    fun every_destination_maps_to_the_shell_route_that_owns_it() {
        // The paths themselves belong to `DriverRoute` (C067). What this asserts is that C068 did
        // not invent one: a typo here is a crash on the first cold start of a release build.
        assertEquals("splash", lk.mageride.driver.nav.DriverRoute.Splash.path)
        assertEquals("onboarding/lang-city", OnboardingDestination.LANGUAGE_CITY.route.path)
        assertEquals("login", OnboardingDestination.LOGIN.route.path)
        assertEquals("profile-setup", OnboardingDestination.PROFILE_SETUP.route.path)
        assertEquals("permissions", OnboardingDestination.PERMISSIONS.route.path)
        assertEquals("home", OnboardingDestination.HOME.route.path)
    }
}
