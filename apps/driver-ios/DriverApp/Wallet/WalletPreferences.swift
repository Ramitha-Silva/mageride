import Foundation
import MageRideShared

/// The **driver-set** low-balance threshold, and why it lives on the handset.
///
/// D2' §SCR-DI-021 draws the warning at *"balance ≤ driver-set threshold (default Rs 200)"* and the
/// C073/C091 deliverable asks for the setting. D5' §9.4 fixes the same Rs 200 and calls it
/// **admin-configurable** — and **no route on the platform stores a per-driver figure**: `iam.yaml`'s
/// profile carries no such field, wallet-svc has no preferences surface, and the only threshold the
/// server knows is the one it evaluates its own `LOW_BALANCE` push against.
///
/// So the two are kept apart rather than reconciled. The **push** is the admin's threshold and this
/// app never sees it; the **on-screen nudge** is the driver's, stored here, defaulting to
/// `WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD`. A driver who works a Rs 300/day van and wants to be
/// warned at Rs 600 gets that on their own screen, which is what the wireframe promises, and nothing
/// about it claims to have changed a server-side rule. Recorded as a spec gap in the C073 handoff and
/// carried forward unchanged.
///
/// A protocol with a `UserDefaults` implementation for the same reason ``ActiveVehicleStore`` is one:
/// a model test has no store, and a fake is what makes the threshold settable.
protocol WalletPreferences: AnyObject {

    /// Minor units. `nil` means the driver has never set one and D5' §9.4's default applies.
    ///
    /// Nullable rather than pre-seeded with 20,000 so *"never changed it"* stays distinguishable from
    /// *"deliberately chose Rs 200"* — the day the platform gains a per-driver setting, the first is
    /// what should be migrated from the server and the second is what should not.
    var lowBalanceThresholdMinor: Int64? { get set }
}

extension WalletPreferences {

    /// The threshold as it will actually be applied — the driver's figure, or D5' §9.4's Rs 200.
    var lowBalanceThreshold: Money {
        lowBalanceThresholdMinor.map { Money.companion.ofMinor(amountMinor: $0) }
            ?? WalletRules.shared.DEFAULT_LOW_BALANCE_THRESHOLD
    }
}

/// ``WalletPreferences`` over the app's own `UserDefaults` suite.
///
/// Nothing here is a secret — the figure is on the screen that set it — so neither the Keychain nor
/// C018's encrypted database is the right home for it. Local for the same reason
/// ``ActiveVehicleStore`` is: there is no operation to call, and inventing a synthetic one would be
/// worse than being honest about the scope.
final class UserDefaultsWalletPreferences: WalletPreferences {

    private let store: UserDefaults

    init(store: UserDefaults = .standard) {
        self.store = store
    }

    /// `object(forKey:)` rather than `integer(forKey:)`, which answers `0` for an absent key — and
    /// zero is a threshold a driver could plausibly choose. The Android twin needs a `-1` sentinel for
    /// exactly this reason; `UserDefaults` hands back an optional and needs none.
    var lowBalanceThresholdMinor: Int64? {
        get { (store.object(forKey: Keys.threshold) as? NSNumber)?.int64Value }
        set {
            if let newValue {
                store.set(NSNumber(value: newValue), forKey: Keys.threshold)
            } else {
                store.removeObject(forKey: Keys.threshold)
            }
        }
    }

    /// The same value `driver_wallet`'s `low_balance_threshold_minor` holds on Android, prefixed so it
    /// cannot collide with anything else in the standard suite.
    private enum Keys {
        static let threshold = "mageride.wallet.low_balance_threshold_minor"
    }
}
