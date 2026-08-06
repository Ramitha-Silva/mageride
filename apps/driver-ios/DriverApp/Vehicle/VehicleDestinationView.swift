import SwiftUI

/// Cluster 2's four destinations, built from the graph's singletons.
///
/// The one arm ``DriverDestinationView`` gives C087, expanded — split out for the same reason
/// ``OnboardingDestinationView`` is: `body` is an implicit `@ViewBuilder`, so every arm of a `switch`
/// wraps every other arm's type in another `_ConditionalContent`, and four real screens inlined
/// beside twenty-six placeholders is a type the compiler struggles to infer at all.
///
/// **The three moves that replace rather than stack** are `DriverNavHost.kt`'s, path for path: the
/// wizard hands over to SCR-DI-006, and SCR-DI-006 hands on to either My Vehicles or back into the
/// wizard. See ``DriverNavigator/replaceTop(with:)`` for why a swipe back would otherwise re-open a
/// step the driver has already submitted.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct VehicleDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .vehicleOnboarding:
            VehicleOnboardingScreen(
                vehicles: graph.vehicles,
                captures: graph.captures,
                session: graph.vehicleSession,
                onCaptureRequested: { navigator.open(.documentCapture) },
                onSubmitted: { navigator.replaceTop(with: .vehicleOnboardingStatus) },
                // "Back exits the wizard" from Step 1/4 (D2' §SCR-DI-004). Every other step's back
                // is a step backwards and never reaches here.
                onExit: navigator.pop
            )

        case .documentCapture:
            // SCR-DI-005 delivers through `DocumentCaptureCoordinator`; the screen that asked for the
            // capture is still underneath — the takeover is presented over it, not pushed — and
            // collects it there.
            DocumentScannerScreen(
                captures: graph.captures,
                camera: graph.camera,
                onFinished: navigator.closeTakeover
            )

        case .vehicleOnboardingStatus:
            VehicleOnboardingStatusScreen(
                vehicles: graph.vehicles,
                session: graph.vehicleSession,
                onDone: { navigator.replaceTop(with: .vehicles) },
                onResume: { navigator.replaceTop(with: .vehicleOnboarding) }
            )

        case .vehicles:
            VehiclesScreen(
                vehicles: graph.vehicles,
                session: graph.vehicleSession,
                activeVehicle: graph.activeVehicle,
                // The ＋ and a row's Resume are the same navigation: AL-30 decides whether the
                // wizard opens at the next incomplete step or starts a NEW vehicle at Step 1/4, and
                // it decides that in the repository, not here.
                onAddVehicle: { navigator.open(.vehicleOnboarding) },
                onOpenStatus: { navigator.open(.vehicleOnboardingStatus) }
            )

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the four cases above here. The
            // arm exists because `DriverRoute` has thirty cases and Swift wants them all accounted
            // for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
