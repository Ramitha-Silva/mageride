import Combine
import Foundation
import Network

/// Whether the handset has a usable internet path.
///
/// Feeds SCR-PI-032's banner and the live map's `dimmed` state, and nothing else — the app never
/// *decides* anything from it. A passenger in a tunnel keeps the last-known markers on screen and
/// keeps whatever the socket last said (US-15.2); the banner is there so the screen stops looking
/// broken, which is the whole of that requirement.
///
/// `NWPathMonitor` rather than a reachability shim: it is the framework Apple ships for the
/// question, it reports interface changes (Wi-Fi to cellular in a moving vehicle, which is the
/// normal case here) and it costs no polling.
///
/// **`status == .satisfied` is the strongest answer this platform gives.** Android's counterpart
/// asks for `NET_CAPABILITY_VALIDATED`, which is a captive-portal check; iOS has no equivalent
/// predicate on `NWPath`. The nearest signal is `isConstrained`/`isExpensive`, and neither means
/// "this network answers". A hotel captive portal therefore reads as online on iOS and offline on
/// Android — a real Section C asymmetry, recorded by C085 from the other side, and the honest one:
/// inventing a probe request here would be this class deciding what "online" means for the whole
/// app. **The live plane is the second opinion that matters** — `PassengerLiveMap.status` is what a
/// captive portal actually breaks, and SCR-PI-010 reads both.
@MainActor
final class ConnectivityMonitor: ObservableObject {

    /// `true` while a satisfied path is up. Starts optimistic — see ``isOnlineNow``.
    @Published private(set) var isOnline: Bool = true

    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "lk.mageride.passenger.connectivity")

    init() {
        monitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            // `[weak self]` again here, not the handler's: reading the outer binding from inside a
            // nested `Task` is an error on Swift 5, which is what ci.yml's Xcode builds with.
            Task { @MainActor [weak self] in self?.isOnline = satisfied }
        }
        monitor.start(queue: queue)
    }

    deinit {
        monitor.cancel()
    }

    /// A one-shot read, for a caller that is not a view.
    ///
    /// Optimistic before the first callback: `NWPathMonitor` delivers its first path asynchronously,
    /// and a banner that flashes "offline" on every cold start is worse than no banner.
    var isOnlineNow: Bool { isOnline }
}
