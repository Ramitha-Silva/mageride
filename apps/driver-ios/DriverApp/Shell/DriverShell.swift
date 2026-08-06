import MageRideShared
import SwiftUI

/// The application shell: one `TabView`, four `NavigationStack`s, and the three things that sit
/// above every screen regardless of which group owns it.
///
/// 1. **The tab bar** (AL-31) — on the four tab routes and nowhere else, which on this platform
///    means: the pre-session flow replaces it and a takeover covers it.
/// 2. **The offline banner** (US-15.6), which preserves the screen underneath.
/// 3. **The app-update gate** (D-31), which is the only thing here allowed to block.
///
/// D2' §C's scaffolding row — "`Scaffold` + `TopAppBar` + `NavigationBar`" becomes "`NavigationStack`
/// + `.toolbar` + `TabView`" — is this file. The stack is per tab rather than one for the app
/// because that is what a `TabView` means on iOS: a driver deep in the wallet who taps Home and taps
/// Wallet again is back where they were, and the swipe-back gesture only exists inside a stack.
struct DriverShell: View {

    @EnvironmentObject private var navigator: DriverNavigator
    @EnvironmentObject private var connectivity: ConnectivityMonitor
    @StateObject private var model: DriverShellModel

    @MainActor
    init(graph: IosAppGraph, navigator: DriverNavigator) {
        _model = StateObject(wrappedValue: DriverShellModel(graph: graph, navigator: navigator))
    }

    var body: some View {
        ZStack {
            if let preSession = navigator.preSession {
                // Cluster 1 has no tab bar in any wireframe, and it is not "a screen pushed on
                // Home" either — a driver on the OTP screen has no Home to go back to.
                DriverDestinationView(route: preSession)
                    .transition(.opacity)
            } else {
                tabs
            }
        }
        .overlay(alignment: .top) {
            OfflineBanner(isVisible: !connectivity.isOnline)
                // Under the status bar rather than over it: "current screen preserved" (US-15.6)
                // means the banner takes space from the content, not from the system's own row.
                .padding(.top, 0)
        }
        .animation(.easeOut(duration: 0.2), value: connectivity.isOnline)
        .animation(.easeInOut(duration: 0.2), value: navigator.preSession)
        // SCR-DI-005 / 031 / 032 — presented over everything, tab bar included. A driver on an
        // alarm screen must not be one tap from their wallet.
        .fullScreenCover(item: $navigator.takeover) { route in
            DriverDestinationView(route: route)
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
            ForEach(DriverTab.allCases) { tab in
                NavigationStack(path: navigator.path(for: tab)) {
                    DriverDestinationView(route: tab.route)
                        .navigationDestination(for: DriverRoute.self) { route in
                            DriverDestinationView(route: route)
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

/// `fullScreenCover(item:)` needs an `Identifiable`, and ``DriverRoute`` is a value with no id of
/// its own. ``DriverRoute/path`` already **is** its identity — that is the property's whole job —
/// so the conformance is a one-liner rather than a second identifier to keep in step.
extension DriverRoute: Identifiable {
    var id: String { path }
}
