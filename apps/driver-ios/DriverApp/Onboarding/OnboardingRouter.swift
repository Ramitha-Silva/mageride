import Foundation

/// Where a driver belongs right now, as one value.
///
/// SCR-DI-001 is *"boot + driver-info router"* (D2' §B) and its states in the wireframe are
/// "no token → Login · registered/not approved → RegistrationHub · approved+perms → Dashboard".
/// Change 6/22 dissolved RegistrationHub into **Profile Setup** — driver identity, before Home,
/// with no vehicle (AL-27) — so the same three questions produce the five values below.
///
/// Byte-for-byte `apps/driver-android/.../onboarding/OnboardingRouter.kt`'s enum. The parity fence
/// is not a style rule here: the two apps have to agree about *where a driver is* or the same
/// account meets a different first run on a handset it has already been onboarded from.
enum OnboardingDestination: CaseIterable {

    /// SCR-DI-002. First run only: no language and city have been chosen.
    case languageCity

    /// SCR-DI-003. There is no session.
    case login

    /// SCR-DI-003a. Signed in, but `registry.driver_profiles` has no name for this driver.
    case profileSetup

    /// SCR-DI-007. Identity is on file; the permissions have not been asked for.
    case permissions

    /// SCR-DI-010. Nothing is outstanding.
    case home

    /// The shell's route for this destination. ``DriverRoute`` is the only place a path is spelt.
    var route: DriverRoute {
        switch self {
        case .languageCity: return .languageCity
        case .login: return .login
        case .profileSetup: return .profileSetup
        case .permissions: return .permissions
        case .home: return .home
        }
    }
}

/// The first-run gate, as a pure function of four facts.
///
/// Pure on purpose. This is the one piece of C086 that decides what a driver sees on every cold
/// start, it has to agree with what SCR-DI-002, SCR-DI-003 and SCR-DI-003a each do when they
/// finish, and none of that is worth discovering on a handset. Every caller — the splash router,
/// the login screen after a verify, Profile Setup after a save — asks this rather than branching
/// for itself.
enum OnboardingRouter {

    /// - Parameters:
    ///   - signedIn: Whether `AuthSessionManager` holds a session for this surface (AL-08).
    ///   - firstRunComplete: Whether SCR-DI-002 has been answered. Checked **first**: a driver who
    ///     has not chosen a language would otherwise meet the login screen in whatever locale the
    ///     handset happens to be set to, which for most drivers here is not one of the three.
    ///   - profileComplete: Whether `registry.driver_profiles` has a name for this driver
    ///     (US-2.21). Never consulted while signed out — there is nobody to have a profile.
    ///   - permissionsAcknowledged: Whether SCR-DI-007 has been shown. The *grants* belong to the
    ///     OS and are asked again on the dashboard; what is remembered here is only that the driver
    ///     has been through the screen, so a denial does not trap them in it.
    static func next(
        signedIn: Bool,
        firstRunComplete: Bool,
        profileComplete: Bool,
        permissionsAcknowledged: Bool
    ) -> OnboardingDestination {
        guard firstRunComplete else { return .languageCity }
        guard signedIn else { return .login }
        guard profileComplete else { return .profileSetup }
        guard permissionsAcknowledged else { return .permissions }

        // AL-27: a driver reaches Home with **no vehicle**. Nothing about a vehicle is asked here,
        // and adding a gate for one would put the Mode-C wizard back in front of the dashboard.
        return .home
    }
}
