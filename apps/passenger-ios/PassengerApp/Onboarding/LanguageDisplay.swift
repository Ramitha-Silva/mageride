import Foundation
import MageRideShared

/// SCR-PI-002's three rows, and how each is written.
///
/// **The order is US-1.3's, not the enum's.** *"ordered Sinhala (first) → Tamil → English (default
/// highlight = Sinhala)"* — AL-26 makes Sinhala the default rather than a translation of English,
/// and putting it first is half of what that means on screen. `OnboardingModelTests` pins it.
enum LanguageDisplay {

    /// The rows, in the order the wireframe draws them.
    ///
    /// Typed out rather than read off `Language.entries`: the wire enum's declaration order happens
    /// to agree today, and a contract change that reordered it must not silently reorder a screen
    /// whose order is a requirement.
    static let choices: [Language] = [.si, .ta, .en]

    /// AL-26's default — what is highlighted before the passenger touches anything.
    static let `default`: Language = .si

    /// The language's own name in its own script — `සිංහල`, `தமிழ்`, `English`.
    ///
    /// **Deliberately not a string resource.** An endonym is the same string in all three locales,
    /// so the three `.strings` files would carry three identical values — which `LocalizationTests`
    /// correctly rejects as *"a key copied into si and ta and never translated"*. A proper noun is
    /// **data**, not copy. Whoever is looking for their language is looking for the script they read.
    static func endonym(_ language: Language) -> String {
        switch language {
        case Language.ta: return "தமிழ்"
        case Language.en: return "English"
        default: return "සිංහල"
        }
    }

    /// The English gloss beside it, so a passenger who reads neither other script can still
    /// navigate.
    ///
    /// `nil` for English, where the gloss would repeat the endonym — which is exactly what the
    /// wireframe draws: `සිංහල · Sinhala`, `தமிழ் · Tamil`, and a bare `English`.
    static func englishName(_ language: Language) -> String? {
        switch language {
        case Language.ta: return "Tamil"
        case Language.en: return nil
        default: return "Sinhala"
        }
    }
}

// A Kotlin enum reaches Swift as a class, so a `switch` over one has no exhaustiveness check and
// needs a `default`. `Language.si` is the arm those defaults land on above, which is also AL-26's
// answer for a value this build has never heard of — a fourth language added to the contract would
// show its rows in Sinhala rather than crash, and `LanguageDisplay.choices` is what decides which
// three are offered at all.
