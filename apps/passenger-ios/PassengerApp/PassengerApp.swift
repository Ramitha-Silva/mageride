import MageRideShared
import SwiftUI

/// The app.
///
/// One `App`, one shell, one graph — D2' §0.1: *"a screen = … a SwiftUI `View` inside a
/// `NavigationStack`"*. Everything cross-cutting is in ``PassengerShell``; everything shared is in
/// ``PassengerGraph``. Nothing else in this target may start a Koin, keep a second navigator, open a
/// second database handle or dial a second socket.
@main
struct PassengerApp: App {

    /// The delegate exists for exactly the four callbacks SwiftUI has no equivalent for: the APNs
    /// device token, a push delivered while the app is running, a push tapped from the notification
    /// centre, and a **silent** push the system woke the app for — which on this surface is P-02's
    /// `location_request`, the one push that carries no deep link at all.
    @UIApplicationDelegateAdaptor(PassengerAppDelegate.self) private var delegate

    @StateObject private var graph = PassengerGraph()

    var body: some Scene {
        WindowGroup {
            PassengerShell(graph: graph.shared, navigator: graph.navigator, live: graph.live)
                .environmentObject(graph)
                .environmentObject(graph.navigator)
                .environmentObject(graph.connectivity)
                .environmentObject(graph.pushes)
                .environmentObject(graph.live)
                // The PMTiles archive for this build, so no screen has to thread it into a map.
                .environment(\.pmTilesUrl, graph.environment.pmTilesUrl)
                // §0.2's primary is the app's accent, so every system control takes it without a
                // modifier of its own.
                .tint(MageRideColor.primary)
                .task {
                    delegate.attach(graph: graph)
                    graph.warmUp()
                }
                // A push that names a screen. The router keeps its last value, so one that arrived
                // before this view existed is still delivered — the cold-start notification tap.
                .onReceive(graph.pushes.$pending.compactMap { $0 }) { route in
                    graph.navigator.open(route)
                    graph.pushes.consume()
                }
                // The one door a link of any kind comes through. **Nothing routes here today** —
                // this app declares no URL scheme and no associated domain, because every surviving
                // payment rail is in-app (AL-57/AL-59) and `mageride://…` is a value inside a push
                // payload rather than a link the system delivers. It is wired anyway so that the day
                // a domain is added there is one resolver rather than two; see
                // `PassengerApp.entitlements`.
                .onOpenURL { url in
                    graph.pushes.offer(uri: url.absoluteString)
                }
        }
    }
}
