import MageRideShared
import SwiftUI

/// The application shell: one `TabView`, four `NavigationStack`s, and the three things that sit
/// above every screen regardless of which group owns it.
///
/// 1. **The tab bar** — on the four tab roots and nowhere else, which on this platform means: the
///    pre-session flow replaces it and a takeover covers it.
/// 2. **SCR-PI-032's offline banner** (US-15.6), which preserves the screen underneath.
/// 3. **SCR-PI-031's update gate** (D-31) — a wall when the `426` is mandatory, a dismissible banner
///    when it is not.
///
/// D2' §C's scaffolding row — *"`Scaffold` + `TopAppBar` + `NavigationBar`"* becomes *"`NavigationStack`
/// + `.toolbar` + `TabView`"* — is this file. The stack is per tab rather than one for the app
/// because that is what a `TabView` means on iOS: a passenger deep in their trip history who taps
/// Map and taps Trips again is back where they were, and the swipe-back gesture only exists inside a
/// stack.
///
/// **There is no drawer and no `≡`.** `passenger_ios.html`'s SCR-PI-033 is the Menu *tab* over a
/// `List`, where the Android wireframe draws a modal drawer behind a scrim — see ``PassengerTab``
/// for the whole of that Section C delta. Nothing here hosts a drawer, so there is no composition
/// local for opening one and no screen needs to be handed the ability.
struct PassengerShell: View {

    @EnvironmentObject private var navigator: PassengerNavigator
    @EnvironmentObject private var connectivity: ConnectivityMonitor
    @StateObject private var model: PassengerShellModel

    @MainActor
    init(
        graph: IosAppGraph,
        navigator: PassengerNavigator,
        live: PassengerLiveMap,
        identity: PassengerIdentity
    ) {
        _model = StateObject(
            wrappedValue: PassengerShellModel(
                graph: graph,
                navigator: navigator,
                live: live,
                identity: identity
            )
        )
    }

    var body: some View {
        ZStack {
            if let preSession = navigator.preSession {
                // Cluster 1 has no tab bar in any wireframe, and it is not "a screen pushed on the
                // map" either — a passenger on the OTP screen has no map to go back to.
                PassengerDestinationView(route: preSession)
                    .transition(.opacity)
            } else {
                tabs
            }
        }
        // SCR-PI-032's own `Δ iOS` clause: *"`.safeAreaInset` banner; current screen preserved"*.
        // An overlay would cover the top of a full-bleed map; an inset moves it down.
        .safeAreaInset(edge: .top, spacing: 0) {
            OfflineBanner(isVisible: !connectivity.isOnline)
        }
        .animation(.easeOut(duration: 0.2), value: connectivity.isOnline)
        .animation(.easeInOut(duration: 0.2), value: navigator.preSession)
        // SCR-PI-028 / SCR-PI-029 — presented over everything, tab bar included. A passenger on an
        // alarm screen must not be one tap from their trip history.
        .fullScreenCover(item: $navigator.takeover) { route in
            PassengerDestinationView(route: route)
        }
        .updateGate(
            signal: model.upgrade,
            onUpdate: model.openStore,
            onDismiss: model.dismissUpgrade
        )
        .task { model.start() }
    }

    private var tabs: some View {
        TabView(selection: navigator.tabSelection) {
            ForEach(PassengerTab.allCases) { tab in
                NavigationStack(path: navigator.path(for: tab)) {
                    PassengerDestinationView(route: tab.route)
                        .navigationDestination(for: PassengerRoute.self) { route in
                            PassengerDestinationView(route: route)
                        }
                }
                .tabItem {
                    Label {
                        Text(tab.labelKey, bundle: MageRideColor.bundle)
                    } icon: {
                        Image(systemName: tab.symbolName)
                    }
                }
                .tag(tab)
            }
        }
        // §0.2's primary is the app's accent, so the selected tab, every `Toggle` and every system
        // button take it without a modifier of their own. Set here as well as in the asset
        // catalogue's `AccentColor` because a `TabView` inside a `fullScreenCover` does not inherit
        // the target's accent on every iOS version this app supports.
        .tint(MageRideColor.primary)
    }
}

/// `fullScreenCover(item:)` needs an `Identifiable`, and ``PassengerRoute`` is a value with no id of
/// its own. ``PassengerRoute/path`` already **is** its identity — that is the property's whole job —
/// so the conformance is a one-liner rather than a second identifier to keep in step.
extension PassengerRoute: Identifiable {
    var id: String { path }
}
