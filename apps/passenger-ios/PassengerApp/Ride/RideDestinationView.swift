import MageRideShared
import SwiftUI

/// Cluster 4's six destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C098, expanded — same reason
/// ``OnboardingDestinationView``, ``HomeDestinationView`` and ``BookingDestinationView`` exist.
///
/// **The hand-offs are the interesting part of this file.** A ride moves forwards and never
/// backwards, so four of the six moves are a `replaceTop`: SCR-PI-014 is replaced by SCR-PI-015 when
/// a driver accepts, SCR-PI-015 by SCR-PI-016 when the ride completes, and SCR-PI-017 by SCR-PI-018
/// when the money settles. Pushing instead would leave an edge-swipe back into *"finding a driver"*
/// for a ride that has one, or back into a payment screen for a fare that has been paid.
@MainActor
struct RideDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .findingDriver(let rideId):
            FindingDriverScreen(
                rideId: rideId,
                rides: graph.rides,
                live: graph.live,
                onAssigned: { navigator.replaceTop(with: .activeRide(rideId: rideId)) },
                onFinished: { navigator.popToRoot(.map) },
                // US-6A.11's retry. The map is where a booking starts, and the draft is already
                // cleared — SCR-PI-009 cleared it when it booked.
                onRetry: { navigator.popToRoot(.map) }
            )

        case .activeRide(let rideId):
            ActiveRideScreen(
                rideId: rideId,
                rides: graph.rides,
                live: graph.live,
                choice: graph.callChoice,
                contact: graph.rideContact,
                onFinished: { navigator.popToRoot(.map) },
                onPayFare: { navigator.replaceTop(with: .paymentMethod(rideId: rideId)) },
                onReceipt: { navigator.replaceTop(with: .tripSummary(rideId: rideId)) },
                onFreeCall: { navigator.open(.voipCall(rideId: rideId)) },
                onSos: { navigator.open(.sos(rideId: rideId)) }
            )

        case .paymentMethod(let rideId):
            PaymentMethodScreen(
                rideId: rideId,
                rides: graph.rides,
                sessions: graph.sessions,
                preferences: graph.preferences,
                selection: graph.paymentSelection,
                onConfirmed: { _ in navigator.open(.payFare(rideId: rideId)) },
                // **There is no passenger wallet screen anywhere on this platform.** SCR-PI-025 is
                // Mode B *subscriptions*, and sending a passenger who is Rs 40 short of a fare to
                // their subscription cards would be worse than doing nothing. No SCR-PI id, no
                // wireframe cell and no D2' section draws a passenger top-up, even though AL-57 made
                // the wallet the card-acceptance rail. C082 recorded the gap from the Android side;
                // this is the one line that changes when the screen exists.
                onTopUp: { }
            )

        case .payFare(let rideId):
            PayFareScreen(
                rideId: rideId,
                // The rail SCR-PI-016 confirmed. A route argument cannot carry it — see
                // ``PaymentSelection``, and the C080 defect its note records.
                method: graph.paymentSelection.rail(for: rideId),
                rides: graph.rides,
                camera: graph.camera,
                bank: graph.bankApp,
                onBack: { navigator.pop() },
                onSupport: { navigator.open(.support) },
                onSettled: {
                    graph.paymentSelection.forget(rideId: rideId)
                    navigator.replaceTop(with: .tripSummary(rideId: rideId))
                }
            )

        case .tripSummary(let rideId):
            TripSummaryScreen(
                rideId: rideId,
                rides: graph.rides,
                ratings: graph.ratings,
                onClose: { navigator.popToRoot(.map) },
                onPay: { navigator.open(.paymentMethod(rideId: rideId)) },
                onRate: { navigator.open(.rateDriver(rideId: rideId)) }
            )

        case .rateDriver(let rideId):
            RateDriverScreen(
                rideId: rideId,
                rides: graph.rides,
                ratings: graph.ratings,
                // Back to the receipt, which re-reads the queue on appear and stops offering the
                // rating it has just taken.
                onDone: { navigator.pop() }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the six cases above here.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
