import SwiftUI

/// Cluster 1's five destinations, built from the graph's singletons.
///
/// The one arm ``DriverDestinationView`` gives C086, expanded. Splitting it out is not tidiness:
/// `body` is an implicit `@ViewBuilder`, so every arm of a `switch` wraps every other arm's type in
/// another `_ConditionalContent`, and five real screens inlined beside thirty placeholders is a
/// type the compiler struggles to infer at all.
///
/// **Every step of cluster 1 is a one-way door**, and ``DriverNavigator/open(_:)`` is what makes it
/// one: a pre-session route *replaces* the root rather than stacking on it, so Back from Profile
/// Setup cannot return to the OTP screen of a session that already exists, and Back from Home
/// cannot re-enter onboarding at all. Same rule as the Android shell's `replaceOnboarding`,
/// expressed by the destination rather than by the call site.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct OnboardingDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .splash:
            SplashScreen(
                sessions: graph.sessions,
                profiles: graph.profiles,
                preferences: graph.preferences,
                onResolved: { navigator.open($0.route) }
            )

        case .languageCity:
            LanguageCityScreen(
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
                captures: graph.captures,
                // AL-43: the scanner is SCR-DI-005's and C087 owns it. Profile Setup stays where it
                // is — a takeover is presented OVER the pre-session root — so the captured image
                // returns to the form it belongs to.
                onCaptureRequested: { navigator.open(.documentCapture) },
                onComplete: { navigator.open(.permissions) }
            )

        case .permissions:
            PermissionsScreen(
                permissions: graph.permissions,
                preferences: graph.preferences,
                onContinue: { navigator.open(.home) }
            )

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the five cases above here, and
            // ``DriverRoute/isPreSession`` is the same five. The arm exists because `DriverRoute`
            // has thirty cases and Swift wants them all accounted for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
