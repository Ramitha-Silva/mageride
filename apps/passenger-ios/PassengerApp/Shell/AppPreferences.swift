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
/// between platforms is not the only thing the two apps disagree about. ``lastCallType`` and
/// ``callNumberNoticeShown`` arrived with C098, which is the component that first asks them —
/// SCR-PI-015a is the screen with the memory, exactly as the Info.plist's camera key arrived with the
/// commit that opens a camera. A stored answer nothing asks for is state nobody maintains.
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

    /// SCR-PI-015a's remembered choice — `CallType.wire`, or `nil` before the first call (Δ C098).
    ///
    /// The cell says the sheet *"remembers last choice"*, and it has to **outlive the ride**: a
    /// passenger who always calls normally should not be asked again on their next trip. A wire
    /// string rather than a `CallType` for ``defaultPaymentMethod``'s reason — a value a later build
    /// wrote and this one has never heard of reads as *no preference* rather than as a crash.
    var lastCallType: String? { get set }

    /// Whether US-26.5's *"your number is visible to the other party"* notice has been shown
    /// (Δ C098).
    ///
    /// **Once, and only before a direct dial.** AL-48 withdrew masking outright, so this disclosure
    /// is the transparency that replaced it; showing it before a *free* call would be warning about
    /// something that is not happening, which is how people learn to dismiss disclosures.
    var callNumberNoticeShown: Bool { get set }
}

extension AppPreferences {

    /// Whether SCR-PI-002 has been answered — *"first-launch only"* (D2' §A). Δ C095.
    ///
    /// **Derived rather than stored**, which is the same call `AppPreferences.kt` makes. A separate
    /// flag would be a second fact that can disagree with the first: a passenger with a stored
    /// language and a `false` flag would be sent back to a screen that has nothing left to ask them,
    /// and one with no language and a `true` flag would meet the login screen in whatever locale the
    /// handset happens to be set to — which for most users here is not one of the three (AL-26).
    ///
    /// This is why ``OnboardingModel/finish()`` writes the language **unconditionally**, including
    /// when the passenger accepted the Sinhala default without touching a box: accepting it *is*
    /// answering the screen.
    var firstRunComplete: Bool { language != nil }
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

    var lastCallType: String? {
        get { defaults.string(forKey: Keys.lastCallType) }
        set { defaults.set(newValue, forKey: Keys.lastCallType) }
    }

    var callNumberNoticeShown: Bool {
        get { defaults.bool(forKey: Keys.callNumberNotice) }
        set { defaults.set(newValue, forKey: Keys.callNumberNotice) }
    }

    private enum Keys {
        static let language = "language"
        static let pendingSync = "language_pending_sync"
        static let locationRationale = "location_rationale_acknowledged"
        static let defaultPayment = "default_payment_method"
        static let lastCallType = "last_call_type"
        static let callNumberNotice = "call_number_notice_shown"
    }
}
