import MageRideShared
import SwiftUI

/// Cluster 3's five destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C097, expanded — same reason
/// ``OnboardingDestinationView`` and ``HomeDestinationView`` exist.
///
/// **Every `CaptureTarget` is parked here rather than in a screen.** SCR-PI-008 is one picker
/// reached from five places and it cannot tell which; whoever opens it says what for, and
/// ``BookingDraft/capture(_:)`` puts the answer where it belongs. Putting `expect(_:)` at the
/// navigation site rather than inside each screen is what keeps that pairing readable: the call that
/// opens the picker and the call that says why are the same two lines.
@MainActor
struct BookingDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .rideBooking:
            RideBookingScreen(
                draft: graph.draft,
                bookings: graph.bookings,
                keys: graph.idempotencyKeys,
                onBack: { navigator.pop() },
                onEditRoute: {
                    graph.draft.expect(.bookingDropoff)
                    navigator.open(.searchLocation)
                },
                onProxyDetails: { navigator.open(.proxyRider) },
                onPackageBooking: { navigator.open(.packageBooking) },
                onSchedule: { navigator.open(.scheduleRide) },
                // C098 owns SCR-PI-014; the ride exists from `POST /v1/rides/request` on, so the
                // destination is correct even though the screen is still a placeholder.
                onBooked: { navigator.replaceTop(with: .findingDriver(rideId: $0)) },
                // *"Track route"* — the line is already drawn on the booking map behind this sheet,
                // and SCR-PI-010 is where a route is watched. Back to the map is the whole action.
                onTrackRoute: { navigator.popToRoot(.map) }
            )

        case .proxyRider:
            ProxyRiderScreen(
                draft: graph.draft,
                bookings: graph.bookings,
                live: graph.live,
                onBack: { navigator.pop() },
                onSearch: {
                    graph.draft.expect(.proxyPickup)
                    navigator.open(.searchLocation)
                },
                onDone: { navigator.pop() }
            )

        case .packageBooking:
            PackageBookingScreen(
                draft: graph.draft,
                bookings: graph.bookings,
                keys: graph.idempotencyKeys,
                otps: graph.packageOtps,
                onBack: { navigator.pop() },
                onSearch: { end in
                    graph.draft.expect(end == .pickup ? .packagePickup : .packageDropoff)
                    navigator.open(.searchLocation)
                },
                onBooked: { navigator.replaceTop(with: .packageTracking(rideId: $0)) }
            )

        case .scheduleRide:
            ScheduleRideScreen(
                draft: graph.draft,
                bookings: graph.bookings,
                onBack: { navigator.pop() },
                onPickDestination: {
                    graph.draft.expect(.scheduleDropoff)
                    navigator.open(.searchLocation)
                },
                // A scheduled ride is not a ride yet — it is a `dispatch.scheduled_rides` row that
                // materialises at T-30. C099's SCR-PI-022 is where it is seen; until then the
                // passenger returns to the map, which is where they started.
                onScheduled: { navigator.popToRoot(.map) }
            )

        case .confirmPickup(let requestId):
            ConfirmPickupScreen(
                requestId: requestId,
                bookings: graph.bookings,
                locations: graph.locations,
                // **The name the wireframe prints does not reach this screen**, on either platform.
                // `PushRouter` builds the destination from `data.requestId` alone and the route
                // table is diffed against Android's, so there is nowhere to put `data.bookerName`.
                // The copy falls back to *"Someone"* rather than inventing one. See the C097 handoff.
                bookerName: nil,
                onFinished: { navigator.pop() }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the five cases above here.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
