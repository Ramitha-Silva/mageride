import MageRideShared
import SwiftUI

/// Cluster 7's four destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives C101, expanded — same reason
/// ``OnboardingDestinationView``, ``HomeDestinationView``, ``BookingDestinationView``,
/// ``RideDestinationView``, ``HistoryDestinationView`` and ``SubscriptionDestinationView`` exist.
///
/// **Nothing here replaces anything, and one thing pops.** Every move in this cluster is an ordinary
/// push — the Menu tab is a tab root and the other three are screens a passenger walked to — except
/// SCR-PI-027b's save, which **pops** rather than pushing SCR-PI-027: the passenger came *from* that
/// screen, its card is already reading ``PassengerIdentity`` and has the new name, and pushing a
/// second copy would leave two Settings screens on the Menu stack.
///
/// **`.menu` is a tab root**, so it is reached through ``PassengerTab/route`` rather than by a push;
/// its rows open the other three plus C100's two and C102's support (``PassengerMenuDestination``).
@MainActor
struct SettingsDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .menu:
            MenuScreen(identity: graph.identity) { navigator.open($0) }

        case .savedAddresses:
            SavedAddressesScreen(
                addresses: graph.addresses,
                // The opening camera, without a fifth subscriber on a cold `CLLocationManager` —
                // see ``LastKnownFix`` and ``SavedAddressesModel/init(addresses:lastFix:keys:)``.
                lastFix: graph.lastFix,
                keys: graph.idempotencyKeys
            )

        case .settings:
            SettingsScreen(
                profiles: graph.profiles,
                identity: graph.identity,
                preferences: graph.preferences,
                sessions: graph.sessions,
                onEditProfile: { navigator.open(.editProfile) },
                // Both *"Save Home & Work"* and the Menu tab's own row land here; SCR-PI-026 draws
                // the Home and Work rows at the top precisely so the first of them arrives somewhere
                // useful.
                onSavedAddresses: { navigator.open(.savedAddresses) },
                onSupport: { navigator.open(.support) }
            )

        case .editProfile:
            EditProfileScreen(
                profiles: graph.profiles,
                contacts: graph.sosContacts,
                identity: graph.identity,
                keys: graph.idempotencyKeys,
                onSaved: { navigator.pop() }
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the four cases above here.
            PlaceholderScreen(screen: route.path, route: route)
        }
    }
}
