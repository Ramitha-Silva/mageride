import Foundation
import MageRideShared

/// The device-local answers this app keeps outside its database.
///
/// **Everything here is a *device* setting rather than an account one**, and each is here because
/// there is no route that stores it: AL-26 makes the interface language a device-first choice the
/// server write is allowed to lag, SCR-PI-005's rationale is a first-run fact, and C101's default
/// payment rail has no value in `iam.yaml`'s `DefaultPaymentMethod` enum for `wallet` at all (the
/// contract gap C076 recorded, unchanged).
///
/// A protocol with a `UserDefaults` implementation for the reason the Android twin is an interface:
/// a model test needs to set a value rather than reach for a suite, and the seam is what makes the
/// language rules assertable without a running app.
///
/// The keys are `apps/passenger-android/.../shell/AppPreferences.kt`'s, so a passenger who moves
/// between platforms is not the only thing the two apps disagree about. `lastCallType` and
/// `callNumberNoticeShown` are deliberately **absent** until C102 needs them, for the reason the
/// Info.plist keeps `NSCameraUsageDescription` out: a stored answer nothing asks for is state
/// nobody maintains.
protocol AppPreferences: AnyObject {

    /// SCR-PI-002's answer. `nil` on a first run, which is what makes AL-26's Sinhala default a
    /// *pre-selection* rather than a stored value nobody chose.
    var language: Language? { get set }

    /// Whether the chosen language still has to be written to `iam.users` (AL-26).
    ///
    /// Set when the device is written and the server is not — SCR-PI-002 runs before there is a
    /// session at all, so the first write is always local-only. C095's next authenticated pass
    /// clears it.
    var languagePendingSync: Bool { get set }

    /// Whether SCR-PI-005's rationale has been shown. The *rationale*, not the grant: iOS owns the
    /// grant and answers it through `CLLocationManager.authorizationStatus`, and a second copy of
    /// that here would be a stale one the first time somebody changed it in Settings.
    var locationRationaleAcknowledged: Bool { get set }

    /// C101's default payment rail (US-22.4). A string rather than a typed enum because the
    /// contract's own enum cannot express what this app offers — see the class documentation.
    var defaultPaymentMethod: String? { get set }
}

/// ``AppPreferences`` over `UserDefaults`.
///
/// The standard suite rather than a named one: this app has no extension and no app group, and a
/// suite that is not `.standard` is one more thing that has to be right for `AppleLanguages` — which
/// ``PassengerLocale`` writes — to be read by the system on the next launch.
final class UserDefaultsAppPreferences: AppPreferences {

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var language: Language? {
        // `Language.companion.fromWire` rather than a table here: a value written by a later build
        // answers `nil`, which re-asks SCR-PI-002 — better than silently drawing the app in a
        // language the passenger did not pick, and better than a second copy of the three codes.
        get { defaults.string(forKey: Keys.language).flatMap { Language.companion.fromWire(wire: $0) } }
        set { defaults.set(newValue?.wire, forKey: Keys.language) }
    }

    var languagePendingSync: Bool {
        get { defaults.bool(forKey: Keys.pendingSync) }
        set { defaults.set(newValue, forKey: Keys.pendingSync) }
    }

    var locationRationaleAcknowledged: Bool {
        get { defaults.bool(forKey: Keys.locationRationale) }
        set { defaults.set(newValue, forKey: Keys.locationRationale) }
    }

    var defaultPaymentMethod: String? {
        get { defaults.string(forKey: Keys.defaultPayment) }
        set { defaults.set(newValue, forKey: Keys.defaultPayment) }
    }

    private enum Keys {
        static let language = "language"
        static let pendingSync = "language_pending_sync"
        static let locationRationale = "location_rationale_acknowledged"
        static let defaultPayment = "default_payment_method"
    }
}
