import SwiftUI

/// C092's four destinations, in one arm of ``DriverDestinationView``.
///
/// A sub-view rather than four inline screens, for the reason every other cluster takes one arm:
/// `body` is an implicit `@ViewBuilder`, so every arm of that switch becomes another layer of
/// `_ConditionalContent` around every other arm's type, and four real screens inlined there would cost
/// the compiler far more than a switch over four cases in here.
///
/// **All four hang off the Menu tab** (``DriverRoute/tab``), which is what makes the system back button
/// say `‹ Menu` on each of them — the label SCR-DI-028's own cell draws.
///
/// The three navigations SCR-DI-029 offers are resolved here rather than inside the screen: the profile
/// knows *"open the driver's vehicles"*, and which ``DriverRoute`` that is belongs to the route table.
@MainActor
struct ProfileDestinationView: View {

    let route: DriverRoute

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var navigator: DriverNavigator

    var body: some View {
        switch route {
        case .trackerPairing:
            TrackerPairingScreen(model: graph.makeTrackerPairingModel())

        case .sharing:
            SharingScreen(model: graph.makeSharingModel())

        case .profile:
            DriverProfileScreen(
                model: graph.makeDriverProfileModel(),
                onOpenVehicles: { navigator.open(.vehicles) },
                // *"Per-trip ratings"* is not a screen of its own: the ratings live on the trips, and
                // SCR-DI-030 is where a driver reads and leaves them (US-18.3).
                onOpenRatings: { navigator.open(.rideHistory) },
                onOpenLevel: { navigator.open(.driverLevel) }
            )

        case .rideHistory:
            RideHistoryScreen(model: graph.makeRideHistoryModel())

        default:
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
