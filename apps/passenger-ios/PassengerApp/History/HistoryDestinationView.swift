import MageRideShared
import SwiftUI

/// Cluster 5's three destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C099, expanded — same reason
/// ``OnboardingDestinationView``, ``HomeDestinationView``, ``BookingDestinationView`` and
/// ``RideDestinationView`` exist.
///
/// **Nothing here replaces anything.** Unlike C098's, none of these three is a hand-off: a history
/// screen is somewhere a passenger *went*, and the back stack should take them out the way they came
/// in. `.packageTracking` is the exception worth stating — it is reached from a push as well as from
/// the Packages tab, and ``PassengerRoute/tab`` puts it on the **Trips** stack either way, so a
/// notification tapped while the map is showing lands on a screen with a back button that goes
/// somewhere real.
@MainActor
struct HistoryDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .packageTracking(let rideId):
            PackageTrackScreen(
                rideId: rideId,
                history: graph.history,
                live: graph.live,
                otps: graph.packageOtps,
                // SCR-PI-020 or SCR-PI-021, decided from the ride. A recipient has no session at all
                // — they arrived from a push (P-09, AL-45) — so `nil` here is a *recipient*, which is
                // what ``PackageTrackModel/party(of:signedInUserId:)`` answers.
                signedInUserId: graph.sessions.userId,
                choice: graph.callChoice,
                contact: graph.rideContact,
                onFreeCall: { navigator.open(.voipCall(rideId: rideId)) }
            )

        case .trips:
            TripHistoryScreen(
                history: graph.history,
                sessions: graph.sessions,
                choice: graph.callChoice,
                contact: graph.rideContact,
                onOpenTrip: { tripId in navigator.open(.tripDetails(tripId: tripId)) },
                onFreeCall: { rideId in navigator.open(.voipCall(rideId: rideId)) }
            )

        case .tripDetails(let tripId):
            TripDetailsScreen(
                tripId: tripId,
                history: graph.history,
                sessions: graph.sessions,
                // `⚑ Report` → SCR-PI-030. The support screen has no per-trip argument on either
                // platform: `support.yaml`'s ticket takes a `relatedRideId` and the wireframe's own
                // *"Attached trip"* line is C102's to wire, which is why this opens the tab root.
                onReportIssue: { navigator.open(.support) }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the three cases above here.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
