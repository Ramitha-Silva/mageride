import Combine
import SwiftUI

/// The app's whole navigation state: which tab, and what is pushed on each tab's stack.
///
/// **One navigator, one stack per tab.** SwiftUI's `NavigationStack` keeps its own path, and a
/// `TabView` of four stacks has four — which is the behaviour a tab bar is supposed to have (a
/// driver deep in the wallet who taps Home and taps Wallet again is back where they were). Holding
/// the four paths here rather than in four `@State`s is what lets a push arrive from outside any
/// view: a notification tap, a `mageride://` link, a session that ended.
///
/// The Android shell does the same job with `NavHostController`; the difference is that
/// navigation-compose owns the back stack and SwiftUI hands it to the app, so this class exists
/// where Android had none.
@MainActor
final class DriverNavigator: ObservableObject {

    /// The selected tab. Setting it is what a tab tap does.
    @Published var tab: DriverTab = .home

    /// Per-tab push stacks, keyed by tab. `NavigationPath` erases the element type, which is what
    /// lets a stack hold ``DriverRoute`` today and a screen group's own value type tomorrow.
    @Published var paths: [DriverTab: NavigationPath] = [:]

    /// The pre-session flow, drawn in place of the whole `TabView` while it is set.
    ///
    /// Cluster 1 has no tab bar in any wireframe, and it is not "a screen pushed on Home" either —
    /// a driver on the OTP screen has no Home to go back to. ``DriverShell`` swaps the root.
    @Published var preSession: DriverRoute? = .splash

    /// A destination presented over everything, tab bar included — SCR-DI-005, 031 and 032.
    @Published var takeover: DriverRoute?

    /// Pushes [route] onto the stack it belongs to, switching tabs first if it belongs to another.
    ///
    /// A takeover is presented rather than pushed, and a pre-session destination replaces the root
    /// rather than either — ``DriverRoute/isFullScreenTakeover`` and ``DriverRoute/isPreSession``
    /// are properties of the destination, so no call site has to know which kind it is holding.
    func open(_ route: DriverRoute) {
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
        // `mageride://wallet` deep link taken while the wallet is already showing stacks the wallet
        // on the wallet and the back button goes nowhere visible.
        guard route != route.tab.route else {
            popToRoot(route.tab)
            return
        }

        var path = paths[route.tab] ?? NavigationPath()
        path.append(route)
        paths[route.tab] = path
    }

    /// Empties one tab's stack. A second tap on the selected tab does this, as iOS users expect.
    func popToRoot(_ tab: DriverTab) {
        paths[tab] = NavigationPath()
    }

    /// Pops the destination on top of the selected tab's stack.
    ///
    /// For a screen whose Back is its **own** — the Mode-C wizard's, which steps between four bodies
    /// before it leaves at all (D2' §SCR-DI-004) — and which therefore hides the system's.
    func pop() {
        guard var path = paths[tab], !path.isEmpty else { return }
        path.removeLast()
        paths[tab] = path
    }

    /// Replaces the destination on top of the selected tab's stack with [route].
    ///
    /// The SwiftUI equivalent of `navigate(x) { popUpTo(current) { inclusive = true } }`, which is
    /// what `DriverNavHost.kt` writes for the same three moves: the wizard handing over to
    /// SCR-DI-006, and SCR-DI-006 handing on to either My Vehicles or the wizard. Replacing rather
    /// than stacking is what stops a swipe back from SCR-DI-006 re-opening a step the driver has
    /// already submitted.
    ///
    /// A cross-tab [route] cannot replace anything on this tab, so it is opened normally.
    func replaceTop(with route: DriverRoute) {
        guard route.tab == tab, !route.isPreSession, !route.isFullScreenTakeover else {
            open(route)
            return
        }
        pop()
        open(route)
    }

    /// Dismisses whatever is presented over the whole app — SCR-DI-005 once it has delivered, and
    /// the two C093 takeovers when they end.
    func closeTakeover() {
        takeover = nil
    }

    /// Drops everything and returns to [route].
    ///
    /// C014 raises `RouteToLogin` for every way a session can end — logout, refresh failure,
    /// `403 device-revoked` (AL-08), PDPA erasure — and what is on the stacks belongs to a driver
    /// who is no longer signed in.
    func reset(to route: DriverRoute) {
        paths = [:]
        takeover = nil
        tab = .home
        preSession = route.isPreSession ? route : nil
        if !route.isPreSession { open(route) }
    }

    /// Binding for one tab's stack, for `NavigationStack(path:)`.
    func path(for tab: DriverTab) -> Binding<NavigationPath> {
        Binding(
            get: { self.paths[tab] ?? NavigationPath() },
            set: { self.paths[tab] = $0 }
        )
    }

    /// The tab-selection binding. A tap on the tab already selected pops that tab to its root.
    var tabSelection: Binding<DriverTab> {
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
