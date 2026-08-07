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

    /// SCR-PI-008's *"Select on map"*. A sheet rather than a destination because no SCR-PI id names
    /// a map picker — see ``MapPickSheet``.
    @State private var isMapPickerOpen = false

    var body: some View {
        switch route {
        case .liveMap:
            LiveMapScreen(
                live: graph.live,
                locations: graph.locations,
                places: graph.places,
                snapshots: graph.nearby,
                recents: graph.recents,
                onSearch: {
                    // The home sheet's *"Where to?"* is a **fresh** booking, so nothing is expected
                    // — `capture` answers `false` and SCR-PI-008 calls `begin` instead (Δ C097).
                    navigator.open(.searchLocation)
                },
                // AL-23 / US-4.6 — a Mode B marker opens the access request with the vehicle already
                // filled in. SCR-PI-007's popup is not what a private vehicle offers.
                onRequestModeBAccess: { navigator.open(.modeBRequest(vehicleId: $0)) },
                // A shortcut and a recent are both destinations, so they go where a chosen place
                // goes: straight into a booking. Tapping ★ Home **is** choosing where to go.
                onPlaceChosen: { place in
                    graph.draft.begin(dropoff: place.toPlace())
                    navigator.open(.rideBooking)
                },
                onAddAddress: { navigator.open(.savedAddresses) }
            )

        case .searchLocation:
            SearchLocationScreen(
                places: graph.places,
                recents: graph.recents,
                // Biased now (Δ C097). The map records what it already subscribed for and this
                // reads it — see ``LastKnownFix`` for why a second subscription would be the wrong
                // way to get one.
                around: graph.lastFix.point,
                // **One picker, five callers.** Whoever opened it parked a `CaptureTarget` on the
                // draft; if nobody did, this is the home sheet's *"Where to?"* and the chosen place
                // begins a booking rather than editing one. See ``CaptureTarget``.
                onPlaceChosen: { place in
                    if graph.draft.capture(place.toPlace()) {
                        navigator.pop()
                    } else {
                        graph.draft.begin(dropoff: place.toPlace())
                        navigator.replaceTop(with: .rideBooking)
                    }
                },
                // The wireframe's *"📌 Select on map"*. **No SCR-PI id exists for a map picker** —
                // the frames offer it as a *method* on three cells and draw a screen for none of
                // them (C079's first gap) — so C097 built one as a `.sheet`, and this is the third
                // caller of it.
                onPickOnMap: { isMapPickerOpen = true },
                onAddAddress: { navigator.open(.savedAddresses) }
            )
            .sheet(isPresented: $isMapPickerOpen) {
                MapPickSheet(
                    titleKey: "search_select_on_map",
                    around: graph.lastFix.point,
                    onUse: { place in
                        // The same two answers a prediction gets: fill in whoever is waiting, or
                        // begin a booking when nobody is.
                        if !graph.draft.capture(place) {
                            graph.draft.begin(dropoff: place)
                            navigator.replaceTop(with: .rideBooking)
                        } else {
                            navigator.pop()
                        }
                    },
                    onDismiss: { isMapPickerOpen = false }
                )
            }

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the two cases above here. The
            // arm exists because `PassengerRoute` has thirty-two cases and Swift wants them all
            // accounted for.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
