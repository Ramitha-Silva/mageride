import Combine
import Foundation
import MageRideShared
import UIKit

/// The three cross-cutting subscriptions no screen can be responsible for.
///
/// 1. **D-31's update gate** — the gateway enforces the minimum version at the edge on every route,
///    so any of the 176 operations can answer `426`; C013 raises it once on
///    `MageRideApiSignals.upgradeRequired` and this is the single subscriber.
/// 2. **The session that ended** — C014 raises `RouteToLogin` for logout, a failed refresh,
///    `403 device-revoked` (AL-08) and PDPA erasure, and the whole stack belongs to a driver who is
///    no longer signed in.
/// 3. **The cold-start version check** — `GET /v1/version/check` is public and unattested precisely
///    so a build too old to authenticate can still learn that it is too old, and it publishes on the
///    **same** signal a mid-session `426` does. One wall, one subscriber, either way in.
///
/// Subscribed exactly once, here. A screen that also handled `RouteToLogin` would race this one and
/// reset the navigation twice.
@MainActor
final class DriverShellModel: ObservableObject {

    /// D-31's payload, or `nil` when no gate is in force.
    @Published var upgrade: UpgradeRequiredSignal?

    private let graph: IosAppGraph
    private let navigator: DriverNavigator
    private var subscriptions: [FlowSubscription] = []
    private var versionCheck: Task<Void, Never>?

    init(graph: IosAppGraph, navigator: DriverNavigator) {
        self.graph = graph
        self.navigator = navigator
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
            self?.navigator.reset(to: .login)
        })


        // Ask before anything else does (Δ C075 on the Android side). Without this the first thing
        // a driver below the floor sees is a login screen whose OTP request failed.
        versionCheck = Task { [graph] in
            // All three arguments are spelled out because a Kotlin default does not survive the
            // export. These ARE `VersionApi`'s own defaults: a `nil` platform and version make
            // `:shared` fall back to `transport.config`, which `IosAppConfig` has already set from
            // `DriverEnvironment` — passing them from here would be a second copy of both.
            //
            // `publishSignal: true` is the load-bearing one. It is what publishes D-31's
            // upgrade-required signal onto `graph.upgrades`, which this class subscribed to above;
            // passing `false` would leave the update gate waiting for an event nothing raises.
            _ = try? await graph.api.version.checkAppVersion(
                platform: nil,
                currentVersion: nil,
                publishSignal: true
            )
        }
    }

    /// Dismisses an **optional** gate. A mandatory one offers no way here — see ``UpdateGate``.
    func dismissUpgrade() {
        upgrade = nil
    }

    /// Opens the App Store link from the `426` payload.
    ///
    /// D2' §C: "App update — in-app update / Play redirect (Android) · **App Store redirect**
    /// (iOS)". `SKStoreProductViewController` would keep the driver in the app, but it cannot show
    /// an *update* affordance — only a product page with an OPEN button — so the redirect is the
    /// honest one. A null or unopenable URL is not an error worth surfacing: the gate itself already
    /// says what is wrong, and a missing store link is the platform's misconfiguration rather than
    /// something the driver can act on.
    func openStore(_ url: String?) {
        guard let url, !url.isEmpty, let target = URL(string: url) else { return }
        UIApplication.shared.open(target)
    }
}

// `SessionEvent.RouteToLogin` reaches Swift as the flattened `SessionEventRouteToLogin`: Kotlin's
// nested types have no Objective-C counterpart, so the exporter concatenates the names. There is
// nothing to declare here — the name above is the generated one, and it is called out because it
// is the first thing that looks like a typo when reading this file next to the Kotlin.
