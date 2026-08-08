import MageRideShared
import SwiftUI

/// C102's three destinations, built from the graph's singletons.
///
/// The one arm ``PassengerDestinationView`` gives this cluster, expanded — same reason
/// ``OnboardingDestinationView``, ``HomeDestinationView``, ``BookingDestinationView``,
/// ``RideDestinationView``, ``HistoryDestinationView``, ``SubscriptionDestinationView`` and
/// ``SettingsDestinationView`` exist.
///
/// **Two of the three are takeovers and one is a tab root**, which is why they share a file rather
/// than a tab: SCR-PI-030 is **tab 3** and reachable from the Menu tab's *Help & support* row,
/// SCR-PI-027's own row, SCR-PI-017's *"Get help"* and SCR-PI-023's *"Report an issue"* — while
/// ``PassengerRoute/voipCall(rideId:)`` and ``PassengerRoute/sos(rideId:)`` are
/// ``PassengerRoute/isFullScreenTakeover`` and are presented over the whole app by
/// ``PassengerShell``. Both takeovers close through ``PassengerNavigator/closeTakeover()`` — the
/// method the shell has carried since C094 for exactly these two — rather than by popping a stack
/// they were never on.
@MainActor
struct CommsDestinationView: View {

    let route: PassengerRoute

    @EnvironmentObject private var graph: PassengerGraph
    @EnvironmentObject private var navigator: PassengerNavigator

    var body: some View {
        switch route {
        case .voipCall(let rideId):
            VoipCallScreen(
                rideId: rideId,
                rides: graph.rides,
                contact: graph.rideContact,
                engine: graph.voip,
                session: graph.callSession,
                onFinished: navigator.closeTakeover
            )

        case .sos(let rideId):
            SosScreen(
                rideId: rideId,
                safety: graph.safety,
                // The same seam SCR-PI-027b edits (C101). An empty list is what makes `POST /v1/sos`
                // answer `400 no-emergency-contact`, and that screen already says so — which is why
                // this one does not explain it again at the moment somebody is pressing an alarm.
                contacts: graph.sosContacts,
                // A live subscription, not ``LastKnownFix``: the countdown starts on the first
                // emission, and its first emission *is* the last known fix. See ``SosModel``.
                locations: graph.locations,
                onFinished: navigator.closeTakeover
            )

        case .support:
            SupportScreen(
                support: graph.support,
                sessions: graph.sessions,
                // AL-26 — the FAQ is asked for in the language the app is **drawing** in, which is
                // this preference and not the profile's. See ``SupportRepository``.
                preferences: graph.preferences
            )

        default:
            // Unreachable: ``PassengerDestinationView`` routes exactly the three cases above here.
            UnreachableRoute(route: route)
        }
    }
}
