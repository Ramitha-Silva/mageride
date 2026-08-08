import Combine
import Foundation
import MageRideShared
import UIKit

/// The four cross-cutting subscriptions no screen can be responsible for.
///
/// 1. **D-31's update gate** — the gateway enforces the minimum version at the edge on every route,
///    so any app-facing operation can answer `426`; C013 raises it once on
///    `MageRideApiSignals.upgradeRequired` and this is the single subscriber.
/// 2. **The cold-start version check** — `GET /v1/version/check` is public and unattested precisely
///    so a build too old to authenticate can still learn that it is too old, and it publishes on the
///    **same** signal a mid-session `426` does. One wall, one subscriber, either way in. Without it
///    the first thing a passenger below the floor sees is a login screen whose OTP request failed.
/// 3. **The session that ended** — C014 raises `RouteToLogin` for logout, a failed refresh,
///    `403 device-revoked` (AL-08) and PDPA erasure, and the whole stack belongs to a passenger who
///    is no longer signed in. The name on SCR-PI-027's and SCR-PI-033's cards belongs to them too,
///    which is why ``PassengerIdentity`` is cleared from here (Δ C101) and not only by the *Log out*
///    row that is one of the four ways in.
/// 4. **The live socket.** `connect()` is idempotent, so calling it here is *"make sure it is up"*
///    rather than *"open one now"*; the only things that take it down are a signed-out session and
///    the process ending. Nothing is **joined** until a position arrives — see
///    ``PassengerLiveMap/onPosition(_:)``.
///
/// Subscribed exactly once, here. A screen that also handled `RouteToLogin` would race this one and
/// reset the navigation twice; one that also called `disconnect()` would take the socket down under
/// a screen that is still drawing markers.
@MainActor
final class PassengerShellModel: ObservableObject {

    /// D-31's payload, or `nil` when no gate is in force.
    @Published var upgrade: UpgradeRequiredSignal?

    private let graph: IosAppGraph
    private let navigator: PassengerNavigator
    private let live: PassengerLiveMap
    private let identity: PassengerIdentity
    private var subscriptions: [FlowSubscription] = []
    private var versionCheck: Task<Void, Never>?

    init(
        graph: IosAppGraph,
        navigator: PassengerNavigator,
        live: PassengerLiveMap,
        identity: PassengerIdentity
    ) {
        self.graph = graph
        self.navigator = navigator
        self.live = live
        self.identity = identity
    }

    deinit {
        // `FlowSubscription` is not tied to any view's lifetime — see IosFlowWatcher. Cancelling
        // here is what stops a replaced shell from holding a closure over the old navigator.
        subscriptions.forEach { $0.cancel() }
        versionCheck?.cancel()
    }

    /// Starts everything. Idempotent — a second call is a no-op, because SwiftUI may run `.task`
    /// again after a scene change and two collectors would show the gate twice.
    func start() {
        guard subscriptions.isEmpty else { return }

        // `upgradeRequired` replays its last value, so subscribing after the failing call still
        // sees it — which is the case on a cold start whose very first request was refused.
        subscriptions.append(graph.upgrades.watch { [weak self] signal in
            self?.upgrade = signal
        })

        subscriptions.append(graph.sessionEvents.watch { [weak self] event in
            guard event is SessionEventRouteToLogin else { return }
            guard let self else { return }
            // The socket's credential was that session's access token (D-29), so it goes down with
            // it. `disconnect()` also forgets the map, which is what stops the next passenger's
            // first frame being drawn over the previous one's vehicles.
            Task { await self.live.disconnect() }
            self.identity.clear()
            self.navigator.reset(to: .login)
        })

        versionCheck = Task { [graph] in
            _ = try? await graph.api.version.checkAppVersion()
        }

        live.connect()
    }

    /// Dismisses an **optional** gate. A mandatory one offers no way here — see ``UpdateGate``.
    func dismissUpgrade() {
        upgrade = nil
    }

    /// Opens the App Store link from the `426` payload.
    ///
    /// D2' §C: *"App update — in-app update / Play redirect (Android) · **App Store redirect**
    /// (iOS)"*, and §SCR-PI-031 says why in one line — *"iOS has no forced in-app update API"*.
    /// `SKStoreProductViewController` would keep the passenger in the app, but it cannot show an
    /// *update* affordance — only a product page with an OPEN button — so the redirect is the honest
    /// one. A null or unopenable URL is not an error worth surfacing: the gate itself already says
    /// what is wrong, and a missing store link is the platform's misconfiguration rather than
    /// something the passenger can act on.
    func openStore(_ url: String?) {
        guard let url, !url.isEmpty, let target = URL(string: url) else { return }
        UIApplication.shared.open(target)
    }
}

// `SessionEvent.RouteToLogin` reaches Swift as the flattened `SessionEventRouteToLogin`: Kotlin's
// nested types have no Objective-C counterpart, so the exporter concatenates the names. There is
// nothing to declare here — the name above is the generated one, and it is called out because it
// is the first thing that looks like a typo when reading this file next to the Kotlin.
