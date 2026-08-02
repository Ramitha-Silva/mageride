package lk.mageride.driver.onboarding

import lk.mageride.driver.nav.DriverRoute

/**
 * Where a driver belongs right now, as one value.
 *
 * SCR-DA-001 is *"boot + driver-info router"* (D2' §B) and its states in the wireframe are
 * "no token → Login · registered/not approved → RegistrationHub · approved+perms → Dashboard".
 * Change 6/22 dissolved RegistrationHub into **Profile Setup** — driver identity, before Home,
 * with no vehicle (AL-27) — so the same three questions produce the five values below.
 */
internal enum class OnboardingDestination {

    /** SCR-DA-002. First run only: no language and city have been chosen. */
    LANGUAGE_CITY,

    /** SCR-DA-003. There is no session. */
    LOGIN,

    /** SCR-DA-003a. Signed in, but `registry.driver_profiles` has no name for this driver. */
    PROFILE_SETUP,

    /** SCR-DA-007. Identity is on file; the runtime permissions have not been asked for. */
    PERMISSIONS,

    /** SCR-DA-010. Nothing is outstanding. */
    HOME,
    ;

    /** The shell's route for this destination. `DriverRoute` is the only place a path is spelt. */
    val route: DriverRoute
        get() = when (this) {
            LANGUAGE_CITY -> DriverRoute.LanguageCity
            LOGIN -> DriverRoute.Login
            PROFILE_SETUP -> DriverRoute.ProfileSetup
            PERMISSIONS -> DriverRoute.Permissions
            HOME -> DriverRoute.Home
        }
}

/**
 * The first-run gate, as a pure function of four facts.
 *
 * Pure on purpose. This is the one piece of C068 that decides what a driver sees on every cold
 * start, it has to agree with what SCR-DA-002, SCR-DA-003 and SCR-DA-003a each do when they
 * finish, and none of that is worth discovering on a handset. Every caller — the splash router,
 * the login screen after a verify, Profile Setup after a save — asks this rather than branching
 * for itself.
 */
internal object OnboardingRouter {

    /**
     * @param signedIn Whether `AuthSessionManager` holds a session for this surface (AL-08).
     * @param firstRunComplete Whether SCR-DA-002 has been answered. Checked **first**: a driver
     *   who has not chosen a language would otherwise meet the login screen in whatever locale
     *   the handset happens to be set to, which for most drivers here is not one of the three.
     * @param profileComplete Whether `registry.driver_profiles` has a name for this driver
     *   (US-2.21). Never consulted while signed out — there is nobody to have a profile.
     * @param permissionsAcknowledged Whether SCR-DA-007 has been shown. The *grants* belong to
     *   the OS and are asked again on the dashboard; what is remembered here is only that the
     *   driver has been through the screen, so a denial does not trap them in it.
     */
    fun next(
        signedIn: Boolean,
        firstRunComplete: Boolean,
        profileComplete: Boolean,
        permissionsAcknowledged: Boolean,
    ): OnboardingDestination = when {
        !firstRunComplete -> OnboardingDestination.LANGUAGE_CITY

        !signedIn -> OnboardingDestination.LOGIN

        !profileComplete -> OnboardingDestination.PROFILE_SETUP

        !permissionsAcknowledged -> OnboardingDestination.PERMISSIONS

        // AL-27: a driver reaches Home with **no vehicle**. Nothing about a vehicle is asked here,
        // and adding a gate for one would put the Mode-C wizard back in front of the dashboard.
        else -> OnboardingDestination.HOME
    }
}
