import SwiftUI

/// Resolving a key against the app's own bundle, in one place.
///
/// Two reasons this is not `NSLocalizedString(key, comment:)` at the call site:
///
/// 1. **`Bundle.main` is the TEST HOST when a test runs**, so a lookup through it finds nothing and
///    the failure reads as a missing string rather than a missing bundle. `MageRideColor.bundle` is
///    resolved off a class in this target and is right in both.
/// 2. **``DriverLocale`` redirects that bundle** when a driver picks a language, so every lookup
///    that goes through it follows SCR-DI-002's answer and one that does not stays in the handset's
///    language. There is no third way to look a string up in this target.
extension String {

    /// This key, resolved in the driver's language.
    var localised: String {
        NSLocalizedString(self, bundle: MageRideColor.bundle, comment: "")
    }

    /// This key as a format string, with its positional arguments filled in.
    ///
    /// `%1$d`-style specifiers only — `LocalizationTests` asserts that every one of them survives
    /// into Sinhala and Tamil, which an unnumbered `%d` cannot promise once a translator reorders
    /// the sentence.
    func localisedFormat(_ arguments: CVarArg...) -> String {
        String(format: localised, arguments: arguments)
    }
}

extension Text {

    /// A `Text` for a key in the driver's language.
    ///
    /// `Text(_:bundle:)` would do the same thing; this exists so a screen never has to name the
    /// bundle, and so the one place that decides which bundle is ``String/localised``.
    init(key: String) {
        self.init(key.localised)
    }
}
