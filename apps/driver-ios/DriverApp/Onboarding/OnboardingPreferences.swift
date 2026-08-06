import Foundation
import MageRideShared

/// The three first-run answers the app needs **before** it has a session.
///
/// SCR-DI-002 asks for a language and an operating city on a handset that has never signed in
/// (`GET /v1/config/cities` is the one public route in the flow), and SCR-DI-007 asks for
/// permissions. None of the three can be written to `iam.users` at the moment they are given, so
/// they are held here and pushed to the server on the first authenticated call — see
/// ``OnboardingRepository/syncPreferences()``.
///
/// A protocol with a `UserDefaults` implementation for the same two reasons the Android twin is an
/// interface: the language is read at launch, before the graph exists, and a model test has no
/// store — a fake is what makes the router's four inputs settable.
protocol OnboardingPreferences: AnyObject {

    /// The chosen UI language. `nil` until SCR-DI-002 has been answered.
    var language: Language? { get set }

    /// The chosen `config.operating_cities` code (US-1.3a). `nil` until SCR-DI-002.
    var operatingCityCode: String? { get set }

    /// Whether the local choices still have to be pushed to `iam.users` after sign-in.
    var preferencesPendingSync: Bool { get set }

    /// Whether SCR-DI-007 has been shown and dismissed. Grants themselves are asked of the OS.
    var permissionsAcknowledged: Bool { get set }
}

extension OnboardingPreferences {

    /// Whether SCR-DI-002 has been answered — "first run only" (D2' §B).
    var firstRunComplete: Bool { language != nil && operatingCityCode != nil }
}

/// ``OnboardingPreferences`` over the app's own `UserDefaults` suite.
///
/// **Not the Keychain and not C018's database.** Nothing here is a secret — a language and a city
/// code are on the screen that set them — and both alternatives are the wrong shape for a value the
/// app reads before it has drawn anything: the Keychain is for credentials and `mobile_db_schema.md`
/// §0.4 makes opening the database an `async` unwrap of a protected key, which the launch path that
/// picks the interface language cannot wait for. Same reasoning, same conclusion, as
/// `AndroidOnboardingPreferences`' `SharedPreferences`.
final class UserDefaultsOnboardingPreferences: OnboardingPreferences {

    private let store: UserDefaults

    init(store: UserDefaults = .standard) {
        self.store = store
    }

    var language: Language? {
        get { store.string(forKey: Keys.language).flatMap { Language.companion.fromWire(wire: $0) } }
        set { store.set(newValue?.wire, forKey: Keys.language) }
    }

    var operatingCityCode: String? {
        get { store.string(forKey: Keys.city) }
        set { store.set(newValue, forKey: Keys.city) }
    }

    var preferencesPendingSync: Bool {
        get { store.bool(forKey: Keys.pendingSync) }
        set { store.set(newValue, forKey: Keys.pendingSync) }
    }

    var permissionsAcknowledged: Bool {
        get { store.bool(forKey: Keys.permissions) }
        set { store.set(newValue, forKey: Keys.permissions) }
    }

    /// The same key strings as `driver_onboarding`'s on Android, prefixed so they cannot collide
    /// with `AppleLanguages` or anything else the system keeps in the standard suite.
    private enum Keys {
        static let language = "mageride.onboarding.language"
        static let city = "mageride.onboarding.operating_city_code"
        static let pendingSync = "mageride.onboarding.preferences_pending_sync"
        static let permissions = "mageride.onboarding.permissions_acknowledged"
    }
}
