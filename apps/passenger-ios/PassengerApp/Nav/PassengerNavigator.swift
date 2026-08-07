import Combine
import SwiftUI

/// The app's whole navigation state: which tab, and what is pushed on each tab's stack.
///
/// **One navigator, one stack per tab.** SwiftUI's `NavigationStack` keeps its own path, and a
/// `TabView` of four stacks has four — which is the behaviour a tab bar is supposed to have (a
/// passenger deep in their trip history who taps Map and taps Trips again is back where they were).
/// Holding the four paths here rather than in four `@State`s is what lets a push arrive from outside
/// any view: a notification tap, a `mageride://` link, a session that ended.
///
/// The Android shell does the same job with `NavHostController`; the difference is that
/// navigation-compose owns the back stack and SwiftUI hands it to the app, so this class exists
/// where Android had none.
@MainActor
final class PassengerNavigator: ObservableObject {

    /// The selected tab. Setting it is what a tab tap does.
    @Published var tab: PassengerTab = .map

    /// Per-tab push stacks, keyed by tab. `NavigationPath` erases the element type, which is what
    /// lets a stack hold ``PassengerRoute`` today and a screen group's own value type tomorrow.
    @Published var paths: [PassengerTab: NavigationPath] = [:]

    /// The pre-session flow, drawn in place of the whole `TabView` while it is set.
    ///
    /// Cluster 1 has no tab bar in any wireframe, and it is not "a screen pushed on the map" either
    /// — a passenger on the OTP screen has no map to go back to. ``PassengerShell`` swaps the root.
    @Published var preSession: PassengerRoute? = .splash

    /// A destination presented over everything, tab bar included — SCR-PI-028 and SCR-PI-029.
    @Published var takeover: PassengerRoute?

    /// Pushes [route] onto the stack it belongs to, switching tabs first if it belongs to another.
    ///
    /// A takeover is presented rather than pushed, and a pre-session destination replaces the root
    /// rather than either — ``PassengerRoute/isFullScreenTakeover`` and
    /// ``PassengerRoute/isPreSession`` are properties of the destination, so no call site has to
    /// know which kind it is holding.
    func open(_ route: PassengerRoute) {
        if route.isPreSession {
            preSession = route
            return
        }
        if route.isFullScreenTakeover {
            takeover = route
            return
        }

        preSession = nil
        tab = route.tab

        // A tab's own root is "select the tab", not "push a second copy of it". Without this, a
        // `mageride://package/{id}` deep link taken while Trips is already showing stacks Trips on
        // Trips and the back button goes nowhere visible.
        guard route != route.tab.route else {
            popToRoot(route.tab)
            return
        }

        var path = paths[route.tab] ?? NavigationPath()
        path.append(route)
        paths[route.tab] = path
    }

    /// Empties one tab's stack. A second tap on the selected tab does this, as iOS users expect.
    func popToRoot(_ tab: PassengerTab) {
        paths[tab] = NavigationPath()
    }

    /// Pops the destination on top of the selected tab's stack.
    ///
    /// For a screen whose Back is its **own** — the booking flow steps between several bodies before
    /// it leaves at all — and which therefore hides the system's.
    func pop() {
        guard var path = paths[tab], !path.isEmpty else { return }
        path.removeLast()
        paths[tab] = path
    }

    /// Replaces the destination on top of the selected tab's stack with [route].
    ///
    /// The SwiftUI equivalent of `navigate(x) { popUpTo(current) { inclusive = true } }`, which is
    /// what `PassengerNavHost.kt` writes for the booking hand-offs: SCR-PI-014 handing over to
    /// SCR-PI-015 when a driver is found, and SCR-PI-017 handing on to the receipt. Replacing rather
    /// than stacking is what stops a swipe back re-opening a step the passenger has already
    /// completed — back into "finding a driver" for a ride that has one.
    ///
    /// A cross-tab [route] cannot replace anything on this tab, so it is opened normally.
    func replaceTop(with route: PassengerRoute) {
        guard route.tab == tab, !route.isPreSession, !route.isFullScreenTakeover else {
            open(route)
            return
        }
        pop()
        open(route)
    }

    /// Dismisses whatever is presented over the whole app — the call and the alarm, when they end.
    func closeTakeover() {
        takeover = nil
    }

    /// Drops everything and returns to [route].
    ///
    /// C014 raises `RouteToLogin` for every way a session can end — logout, refresh failure,
    /// `403 device-revoked` (AL-08), PDPA erasure — and what is on the stacks belongs to a passenger
    /// who is no longer signed in.
    func reset(to route: PassengerRoute) {
        paths = [:]
        takeover = nil
        tab = .map
        preSession = route.isPreSession ? route : nil
        if !route.isPreSession { open(route) }
    }

    /// Binding for one tab's stack, for `NavigationStack(path:)`.
    func path(for tab: PassengerTab) -> Binding<NavigationPath> {
        Binding(
            get: { self.paths[tab] ?? NavigationPath() },
            set: { self.paths[tab] = $0 }
        )
    }

    /// The tab-selection binding. A tap on the tab already selected pops that tab to its root.
    ///
    /// **There is no special case for Menu here, and there would be one on Android.**
    /// `PassengerTab.MenuTab` carries no route there and its tap opens a drawer over whatever is on
    /// screen, so its selection is intercepted rather than applied. `passenger_ios.html` draws
    /// SCR-PI-033 as an ordinary selected tab over a `List`, so on this platform Menu is a
    /// destination like the other three — see ``PassengerTab``.
    var tabSelection: Binding<PassengerTab> {
        Binding(
            get: { self.tab },
            set: { selected in
                if selected == self.tab {
                    self.popToRoot(selected)
                } else {
                    self.tab = selected
                }
            }
        )
    }
}
