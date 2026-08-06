import Combine
import Foundation
import Network

/// Whether the handset has a usable internet path.
///
/// Feeds the offline banner (US-15.6, SCR-DI-035) and nothing else — the app never *decides*
/// anything from it. A driver in a tunnel keeps sampling GPS, keeps buffering to `gps_buffer` and
/// keeps queuing outbox commands regardless of what this says; the banner is there so the screen
/// stops looking broken, which is the whole of that requirement.
///
/// `NWPathMonitor` rather than a reachability shim: it is the framework Apple ships for the
/// question, it reports interface changes (Wi-Fi to cellular in a moving vehicle, which is the
/// normal case here) and it costs no polling.
///
/// **`status == .satisfied` is the strongest answer this platform gives.** Android's counterpart
/// asks for `NET_CAPABILITY_VALIDATED`, which is a captive-portal check; iOS has no equivalent
/// predicate on `NWPath`. The nearest signal is `isConstrained`/`isExpensive`, and neither means
/// "this network answers". A filling-station captive portal therefore reads as online on iOS and
/// offline on Android — a real Section C asymmetry, and the honest one: inventing a probe request
/// here would be this class deciding what "online" means for the whole app.
@MainActor
final class ConnectivityMonitor: ObservableObject {

    /// `true` while a satisfied path is up. Starts optimistic — see ``isOnlineNow``.
    @Published private(set) var isOnline: Bool = true

    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "lk.mageride.driver.connectivity")

    init() {
        monitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            Task { @MainActor in self?.isOnline = satisfied }
        }
        monitor.start(queue: queue)
    }

    deinit {
        monitor.cancel()
    }

    /// A one-shot read, for the publisher deciding whether to publish or buffer.
    ///
    /// Optimistic before the first callback: `NWPathMonitor` delivers its first path asynchronously,
    /// and a banner that flashes "offline" on every cold start is worse than no banner.
    var isOnlineNow: Bool { isOnline }
}
