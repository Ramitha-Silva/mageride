import Foundation
import MageRideShared
import ObjectiveC

/// Applies the language SCR-PI-002 chose to the strings the app renders.
///
/// D-26 makes the language a *user* preference, not the handset's: a passenger on a phone set to
/// English who picks සිංහල must get a Sinhala app, and AL-26 makes Sinhala the **default** rather
/// than a translation of English.
///
/// **Δ Section C — this is the one place iOS has no counterpart to what Android does.** Android
/// wraps `attachBaseContext` and calls `Activity.recreate()`, which re-inflates every resource. iOS
/// has no per-app locale API on the 16.0 floor (`AppleLanguages` is read when the process starts,
/// and Settings' per-app language row is the user's to set, not the app's), and there is no
/// `recreate()` to call — killing and relaunching the app to change a radio button is not something
/// an iOS app may do. So the switch is made in two halves, and both are needed:
///
/// 1. **Now** — the app bundle's string lookups are redirected to the chosen `.lproj`, so every
///    `Text("key")` and `NSLocalizedString` resolves in the new language from the next view that is
///    built. SCR-PI-002 navigates immediately after Continue, so what the passenger sees is the
///    login screen already in their language.
/// 2. **Next launch** — `AppleLanguages` is written, which is what moves everything the app does
///    *not* render itself: the system's own permission sheets (`InfoPlist.strings`), the share
///    sheet, and `Locale.current`, which is what a fare is formatted against. That is the half
///    `Locale.setDefault` does on Android, and on this platform it cannot take effect before the
///    process restarts.
///
/// The redirection is `object_setClass` on the bundle object rather than a `bundle` argument
/// threaded through every call site. It has to be: `MageRideColor.bundle`, `Bundle.main` and every
/// `Text(_:bundle:)` in this target all resolve to the **same** bundle instance, and a screen group
/// that forgot the argument would silently render in the handset's language. One object, one
/// override, and no call site to get wrong.
///
/// This is `apps/driver-ios/DriverApp/Onboarding/DriverLocale.swift` with the preferences seam
/// changed — the technique, the two halves and the reasoning are the same, and they should stay that
/// way. It lives in `Shell/` here because ``AppPreferences`` does, which is C076's layout.
enum PassengerLocale {

    /// The `.lproj` bundle every lookup is answered from, or `nil` for the handset's own choice.
    fileprivate static var override: Bundle?

    /// The language the app is rendering in right now, or `nil` on a first run.
    private(set) static var current: Language?

    /// The locale to format numbers and dates against until the next launch picks it up.
    ///
    /// A screen that formats a fare applies this with
    /// `.environment(\.locale, PassengerLocale.locale)`; after a relaunch `Locale.current` is
    /// already this and the modifier is a no-op.
    static var locale: Locale { current.map { Locale(identifier: $0.wire) } ?? .current }

    /// Redirects the app's strings to [language], or restores the handset's own resolution.
    ///
    /// Idempotent, and safe to call before any view exists — which is where it is called from, so
    /// that the very first frame after a cold start is in the passenger's language rather than
    /// flashing the handset's.
    static func apply(_ language: Language?) {
        current = language

        let bundle = MageRideColor.bundle
        // Swapping the class once and leaving it swapped: `localizedString` falls straight through
        // to `super` while `override` is nil, so an app that has never answered SCR-PI-002 behaves
        // exactly as it did before this file existed.
        if object_getClass(bundle) != LocalisedBundle.self {
            object_setClass(bundle, LocalisedBundle.self)
        }

        override = language
            .flatMap { bundle.path(forResource: $0.wire, ofType: "lproj") }
            .flatMap(Bundle.init(path:))

        // The half that only takes effect on the next launch. Written even when it is the same
        // value, because a passenger who reinstalls has a fresh defaults suite and a stored language.
        if let language {
            UserDefaults.standard.set([language.wire], forKey: appleLanguages)
        } else {
            UserDefaults.standard.removeObject(forKey: appleLanguages)
        }
    }

    /// Applies whatever SCR-PI-002 stored. Called once, before the first frame.
    static func applyStored(_ preferences: AppPreferences) {
        apply(preferences.language)
    }

    private static let appleLanguages = "AppleLanguages"
}

/// The app bundle, with its string table redirected.
///
/// `Bundle` is one of the few Foundation classes whose lookup is a single overridable method, which
/// is why this technique is the standard one for a runtime language switch on iOS rather than a
/// clever trick. The subclass adds no state of its own — the redirect lives on ``PassengerLocale``.
private final class LocalisedBundle: Bundle {

    override func localizedString(forKey key: String, value: String?, table tableName: String?) -> String {
        guard let override = PassengerLocale.override else {
            return super.localizedString(forKey: key, value: value, table: tableName)
        }
        return override.localizedString(forKey: key, value: value, table: tableName)
    }
}
