import SwiftUI

/// Cluster 3's four destinations, built from the graph's singletons.
///
/// The one arm ``DriverDestinationView`` gives C088, expanded — split out for the same reason
/// ``VehicleDestinationView`` is: `body` is an implicit `@ViewBuilder`, so every arm of a `switch` wraps
/// every other arm's type in another `_ConditionalContent`, and four real screens inlined beside
/// twenty-two placeholders is a type the compiler struggles to infer at all.
///
/// **Winning an offer replaces Home rather than stacking on it.** ``DriverNavigator/replaceTop(with:)``
/// is `popUpTo(x) { inclusive = true }`, and it is what stops a swipe back from SCR-DI-015 landing on a
/// standby map for a ride the driver is still on. The same three moves C087 used it for.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct HomeDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .home:
            HomeScreen(
                home: graph.makeHomeModel(),
                offers: graph.makeOfferModel(),
                onOpenDirectional: { navigator.open(.directional) },
                onOpenVehicles: { navigator.open(.vehicles) },
                // Home is the tab's root, so the ride is pushed on top of it rather than replacing it:
                // there is nothing under Home to pop to. `replaceTop` is what SCR-DI-015 itself uses on
                // the way out.
                onOpenRide: { rideId in navigator.open(.activeRide(rideId: rideId)) },
                onOpenLevel: { navigator.open(.driverLevel) },
                onOpenEarnings: { navigator.open(.earnings) }
            )

        case .directional:
            DirectionalScreen(model: graph.makeDirectionalModel())

        case .activeRide(let rideId):
            ActiveRideScreen(
                rideId: rideId,
                model: graph.makeActiveRideModel(rideId: rideId),
                // The ride is over; the driver goes back to the standby map rather than to a screen
                // about a trip that has ended.
                onFinished: navigator.pop,
                onOpenVoipCall: { id in navigator.open(.voipCall(rideId: id)) },
                onOpenSos: { id in navigator.open(.sos(rideId: id)) }
            )

        case .menu:
            MenuScreen(driverId: graph.sessions.userId, onOpen: navigator.open)

        default:
            // Unreachable: ``DriverDestinationView`` routes exactly the four cases above here. The arm
            // exists because `DriverRoute` has thirty cases and Swift wants them all accounted for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
