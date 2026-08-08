import SwiftUI

/// Cluster 1's five destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C095, expanded. Splitting it out is not tidiness:
/// `body` is an implicit `@ViewBuilder`, so every arm of a `switch` wraps every other arm's type in
/// another `_ConditionalContent`, and five real screens inlined beside twenty-seven placeholders is
/// a type the compiler struggles to infer at all.
///
/// **Every step of cluster 1 is a one-way door**, and ``PassengerNavigator/open(_:)`` is what makes
/// it one: a pre-session route *replaces* the root rather than stacking on it, so Back from Profile
/// Setup cannot return to the OTP screen of a session that already exists, and Back from the map
/// cannot re-enter onboarding at all. Same rule as the Android shell's `popUpTo`, expressed by the
/// destination rather than by the call site.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when `SWIFT_STRICT_CONCURRENCY` is raised.
@MainActor
struct OnboardingDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .splash:
            SplashScreen(
                sessions: graph.sessions,
                profiles: graph.profiles,
                rides: graph.activeRides,
                preferences: graph.preferences,
                // The splash is the one screen that answers with a **route** rather than a
                // destination: it may resume an active ride (US-1.14), which is not one of the five
                // first-run outcomes. See ``SplashModel``.
                onResolved: { navigator.open($0) }
            )

        case .onboarding:
            OnboardingScreen(
                repository: graph.onboarding,
                onContinue: { navigator.open(.login) }
            )

        case .login:
            LoginScreen(
                sessions: graph.sessions,
                onboarding: graph.onboarding,
                profiles: graph.profiles,
                preferences: graph.preferences,
                pushTokens: graph.pushTokens,
                onSignedIn: { navigator.open($0.route) }
            )

        case .profileSetup:
            ProfileSetupScreen(
                profiles: graph.profiles,
                preferences: graph.preferences,
                onComplete: { navigator.open(.locationPermission) }
            )

        case .locationPermission:
            PermissionsScreen(
                permissions: graph.locationPermission,
                preferences: graph.preferences,
                onContinue: { navigator.open(.liveMap) }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the five cases above here, and
            // ``PassengerRoute/isPreSession`` is the same five. The arm exists because
            // `PassengerRoute` has thirty-two cases and Swift wants them all accounted for.
            UnreachableRoute(route: route)
        }
    }
}
