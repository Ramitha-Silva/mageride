import MageRideShared
import SwiftUI

/// Cluster 2's two destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C096, expanded — same reason
/// ``OnboardingDestinationView`` exists: `body` is an implicit `@ViewBuilder`, so every arm of that
/// switch wraps every other arm's type in another `_ConditionalContent`.
///
/// **Every navigation out of this cluster is here rather than in a screen.** A shortcut, a recent and
/// a chosen prediction are the same event — *the passenger has said where they are going* — so all
/// three take one callback and this file decides that it opens SCR-PI-009. A Mode B marker is the
/// other one, and AL-23 is what makes it SCR-PI-024 instead of a popup.
@MainActor
struct HomeDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .liveMap:
            LiveMapScreen(
                live: graph.live,
                locations: graph.locations,
                places: graph.places,
                snapshots: graph.nearby,
                recents: graph.recents,
                onSearch: { navigator.open(.searchLocation) },
                // AL-23 / US-4.6 — a Mode B marker opens the access request with the vehicle already
                // filled in. SCR-PI-007's popup is not what a private vehicle offers.
                onRequestModeBAccess: { navigator.open(.modeBRequest(vehicleId: $0)) },
                onPlaceChosen: { _ in navigator.open(.rideBooking) },
                onAddAddress: { navigator.open(.savedAddresses) }
            )

        case .searchLocation:
            SearchLocationScreen(
                places: graph.places,
                recents: graph.recents,
                // **Not biased yet, and that is C078's gap carried across rather than a new one.**
                // The geocoder takes a point to rank results around and the only screen holding one
                // is the map, whose fix dies with its model. The place for a shared last-known fix
                // is C097's — every one of its six booking screens wants *"current location"* — so
                // it is left to the component that needs it rather than invented here. See the C096
                // handoff.
                around: nil,
                onPlaceChosen: { _ in navigator.replaceTop(with: .rideBooking) },
                // The wireframe's *"📌 Select on map"*. **No SCR-PI id exists for a map picker** —
                // the frames offer it as a *method* on three cells and draw a screen for none of
                // them (C078's first gap) — so this returns to the live map, which is the only map
                // in the app today. C097 owns the picker and should route it there.
                onPickOnMap: { navigator.pop() },
                onAddAddress: { navigator.open(.savedAddresses) }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the two cases above here. The
            // arm exists because `PassengerRoute` has thirty-two cases and Swift wants them all
            // accounted for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
