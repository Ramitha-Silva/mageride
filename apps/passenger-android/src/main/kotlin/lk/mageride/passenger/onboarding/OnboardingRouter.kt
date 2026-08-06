package lk.mageride.passenger.onboarding

import lk.mageride.passenger.nav.PassengerRoute

/**
 * Where a passenger belongs right now, as one value.
 *
 * SCR-PA-001 is the boot router, and the wireframe's own state line gives its outputs:
 * *"KMP `auth` validates token → onboarding / login / live_map (resumes active ride via
 * `GET /v1/rides/passenger/{id}/active`)"*. The two the wireframe folds into "live_map" —
 * the first profile and the location rationale — are SCR-PA-004 and SCR-PA-005, which sit between
 * a verified OTP and the map.
 */
internal enum class PassengerDestination {

    /** SCR-PA-002. First launch only: no language has been chosen. */
    ONBOARDING,

    /** SCR-PA-003. There is no session. */
    LOGIN,

    /** SCR-PA-004. Signed in, but `iam.users` has no name for this passenger. */
    PROFILE_SETUP,

    /** SCR-PA-005. Identity is on file; the location rationale has not been shown. */
    LOCATION_PERMISSION,

    /** SCR-PA-010. Nothing is outstanding. */
    LIVE_MAP,
    ;

    /** The shell's route. `PassengerRoute` is the only place a path is spelt. */
    val route: PassengerRoute
        get() = when (this) {
            ONBOARDING -> PassengerRoute.Onboarding
            LOGIN -> PassengerRoute.Login
            PROFILE_SETUP -> PassengerRoute.ProfileSetup
            LOCATION_PERMISSION -> PassengerRoute.LocationPermission
            LIVE_MAP -> PassengerRoute.LiveMap
        }
}

/**
 * The first-run gate, as a pure function of four facts.
 *
 * Pure on purpose. This is the one piece of C077 that decides what a passenger sees on every cold
 * start, it has to agree with what SCR-PA-002, SCR-PA-003 and SCR-PA-004 each do when they finish,
 * and none of that is worth discovering on a handset. Every caller — the splash, the login screen
 * after a verify, Profile Setup after a save — asks this rather than branching for itself.
 *
 * **There is no operating-city gate here, unlike the driver's.** US-1.3a asks "a user" to choose a
 * launch city during onboarding, and only SCR-DA-002 draws one — the passenger wireframe's
 * SCR-PA-002 has a language picker and nothing else, and D2' §SCR-PA-002 lists *"3-slide tutorial
 * (US-1.2) + Si/Ta/En picker (US-1.3)"*. Recorded in the C077 handoff.
 */
internal object OnboardingRouter {

    /**
     * @param signedIn Whether `AuthSessionManager` holds a session for this surface (AL-08).
     * @param firstRunComplete Whether SCR-PA-002 has been answered. Checked **first**: a passenger
     *   who has not chosen a language would otherwise meet the login screen in whatever locale the
     *   handset happens to be set to, which for most users here is not one of the three (AL-26).
     * @param profileComplete Whether `iam.users` has a `firstName` for this passenger (US-1.5).
     *   Never consulted while signed out — there is nobody to have a profile.
     * @param locationAcknowledged Whether SCR-PA-005 has been shown. The *grant* belongs to the OS
     *   and the map asks again; what is remembered is only that the passenger has seen the
     *   rationale, so a denial does not trap them in it.
     */
    fun next(
        signedIn: Boolean,
        firstRunComplete: Boolean,
        profileComplete: Boolean,
        locationAcknowledged: Boolean,
    ): PassengerDestination = when {
        !firstRunComplete -> PassengerDestination.ONBOARDING
        !signedIn -> PassengerDestination.LOGIN
        !profileComplete -> PassengerDestination.PROFILE_SETUP
        !locationAcknowledged -> PassengerDestination.LOCATION_PERMISSION
        else -> PassengerDestination.LIVE_MAP
    }
}
