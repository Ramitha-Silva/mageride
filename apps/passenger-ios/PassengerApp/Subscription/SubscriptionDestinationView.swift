import MageRideShared
import SwiftUI

/// Cluster 6's four destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C100, expanded — same reason
/// ``OnboardingDestinationView``, ``HomeDestinationView``, ``BookingDestinationView``,
/// ``RideDestinationView`` and ``HistoryDestinationView`` exist.
///
/// **Nothing here replaces anything.** Unlike C098's four hand-offs, every move in this cluster is an
/// ordinary push: SCR-PI-024 → SCR-PI-025 is a passenger following a link, and SCR-PI-025 → the pay
/// sheet or the statement is a passenger opening a card. A back gesture out of any of them lands
/// somewhere that is still true.
///
/// **SCR-PI-025a's Done pops rather than pushing SCR-PI-025.** The pay sheet is reached *from* the
/// card list, so popping is what puts the passenger back on a list that re-reads itself on appear —
/// pushing a second copy of SCR-PI-025 would leave two of them on the Menu tab's stack and a back
/// button that walks through a screen the passenger has already left.
@MainActor
struct SubscriptionDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .modeBRequest(let vehicleId):
            ModeBRequestScreen(
                // AL-23's two doors: a Mode B marker tap on SCR-PI-010 hands over the id it just drew
                // (see ``LiveMapModel/onMarkerTapped(_:)``), and the Menu tab's *"Private transport"*
                // row hands over nothing.
                vehicleId: vehicleId,
                subscriptions: graph.subscriptions,
                sessions: graph.sessions,
                keys: graph.idempotencyKeys,
                onOpenSubscriptions: { navigator.open(.subscriptions) }
            )

        case .subscriptions:
            SubscriptionsScreen(
                subscriptions: graph.subscriptions,
                sessions: graph.sessions,
                // D-22's half the client owns: the marker is erased on the unsubscribe's response
                // rather than on the `ShareRevoked` that follows it. See ``SubscriptionsModel``.
                live: graph.live,
                keys: graph.idempotencyKeys,
                onPay: { navigator.open(.subscriptionPayment(subscriptionId: $0)) },
                onHistory: { navigator.open(.subscriptionPayments(subscriptionId: $0)) }
            )

        case .subscriptionPayment(let subscriptionId):
            SubscriptionPayScreen(
                subscriptionId: subscriptionId,
                subscriptions: graph.subscriptions,
                sessions: graph.sessions,
                // AL-15's LankaQR hand-off, extended by C100 to take the link `POST …/pay` minted —
                // see ``BankAppHandoff/openBankApp(url:)``.
                bank: graph.bankApp,
                keys: graph.idempotencyKeys,
                onDone: { navigator.pop() }
            )

        case .subscriptionPayments(let subscriptionId):
            SubscriptionPaymentsScreen(
                subscriptionId: subscriptionId,
                subscriptions: graph.subscriptions,
                sessions: graph.sessions
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the four cases above here.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
