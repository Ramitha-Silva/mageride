import MageRideShared
import SwiftUI

/// The app.
///
/// One `App`, one shell, one graph — D2' §0.1: *"a screen = … a SwiftUI `View` inside a
/// `NavigationStack`"*. Everything cross-cutting is in ``DriverShell``; everything shared is in
/// ``DriverGraph``. Nothing else in this target may start a Koin, keep a second navigator, or open a
/// second database handle.
@main
struct DriverApp: App {

    /// The delegate exists for exactly the three callbacks SwiftUI has no equivalent for: the APNs
    /// device token, a push delivered while the app is running, and a push tapped from the
    /// notification centre. Everything else about launch is in ``DriverGraph``.
    @UIApplicationDelegateAdaptor(DriverAppDelegate.self) private var delegate

    @StateObject private var graph = DriverGraph()

    var body: some Scene {
        WindowGroup {
            DriverShell(graph: graph.shared, navigator: graph.navigator)
                .environmentObject(graph)
                .environmentObject(graph.navigator)
                .environmentObject(graph.connectivity)
                .environmentObject(graph.pushes)
                .environmentObject(graph.positions)
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
                // Universal Links — D2' §C's "Payment deep-link: Universal Links → portal". The
                // return leg from the OnePay portal lands here; it is resolved through the same
                // table a push is, because a link is a link however it arrived.
                .onOpenURL { url in
                    graph.pushes.offer(uri: url.absoluteString)
                }
        }
    }
}
