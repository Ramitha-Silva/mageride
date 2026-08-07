import Foundation

/// Where a passenger belongs right now, as one value.
///
/// SCR-PI-001 is the boot router, and the wireframe's own state line gives its outputs: *"KMP `auth`
/// routes after token check"*, which the Android cell spells out as *"onboarding / login / live_map
/// (resumes active ride via `GET /v1/rides/passenger/{id}/active`)"*. The two the wireframe folds
/// into "live_map" — the first profile and the location rationale — are SCR-PI-004 and SCR-PI-005,
/// which sit between a verified OTP and the map.
enum PassengerDestination: Equatable {

    /// SCR-PI-002. First launch only: no language has been chosen.
    case onboarding

    /// SCR-PI-003. There is no session.
    case login

    /// SCR-PI-004. Signed in, but `iam.users` has no name for this passenger.
    case profileSetup

    /// SCR-PI-005. Identity is on file; the location rationale has not been shown.
    case locationPermission

    /// SCR-PI-010. Nothing is outstanding.
    case liveMap

    /// The shell's route. ``PassengerRoute`` is the only place a path is spelt.
    var route: PassengerRoute {
        switch self {
        case .onboarding: return .onboarding
        case .login: return .login
        case .profileSetup: return .profileSetup
        case .locationPermission: return .locationPermission
        case .liveMap: return .liveMap
        }
    }
}

/// The first-run gate, as a pure function of four facts.
///
/// Pure on purpose. This is the one piece of C095 that decides what a passenger sees on every cold
/// start, it has to agree with what SCR-PI-002, SCR-PI-003 and SCR-PI-004 each do when they finish,
/// and none of that is worth discovering on a handset. Every caller — the splash, the login screen
/// after a verify, Profile Setup after a save — asks this rather than branching for itself.
///
/// **There is no operating-city gate here, unlike the driver's.** US-1.3a asks "a user" to choose a
/// launch city during onboarding, and only SCR-DI-002 draws one — `passenger_ios.html`'s SCR-PI-002
/// has a language picker and nothing else, and D2' §SCR-PA-002 lists *"3-slide tutorial (US-1.2) +
/// Si/Ta/En picker (US-1.3)"*. C077 recorded it as a gap and it is unchanged.
enum OnboardingRouter {

    /// - Parameters:
    ///   - signedIn: Whether ``PassengerSessions`` holds a session for this surface (AL-08).
    ///   - firstRunComplete: Whether SCR-PI-002 has been answered. Checked **first**: a passenger
    ///     who has not chosen a language would otherwise meet the login screen in whatever locale
    ///     the handset happens to be set to, which for most users here is not one of the three
    ///     (AL-26).
    ///   - profileComplete: Whether `iam.users` has a `firstName` for this passenger (US-1.5). Never
    ///     consulted while signed out — there is nobody to have a profile.
    ///   - locationAcknowledged: Whether SCR-PI-005 has been shown. The *grant* belongs to the OS
    ///     and the map asks again; what is remembered is only that the passenger has seen the
    ///     rationale, so a denial does not trap them in it.
    static func next(
        signedIn: Bool,
        firstRunComplete: Bool,
        profileComplete: Bool,
        locationAcknowledged: Bool
    ) -> PassengerDestination {
        if !firstRunComplete { return .onboarding }
        if !signedIn { return .login }
        if !profileComplete { return .profileSetup }
        if !locationAcknowledged { return .locationPermission }
        return .liveMap
    }
}
