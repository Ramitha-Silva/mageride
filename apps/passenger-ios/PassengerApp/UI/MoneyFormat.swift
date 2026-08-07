import Foundation
import MageRideShared

/// Rendering a rupee amount — `Rs 740`, `Rs 1,240`, `Rs 480.50`.
///
/// **`Money` deliberately does not format itself** (C012): rendering needs a locale and a symbol,
/// and no user-facing string may live in `:shared`. This is the app's half of that split, and it is
/// one type so that SCR-PI-009's tier price, SCR-PI-012's estimate and C098's receipt all group
/// their thousands the same way. The same file is `apps/driver-ios/DriverApp/UI/MoneyFormat.swift`
/// and `apps/passenger-android/.../ui/MoneyFormat.kt`; C079's handoff argues why it is not in
/// `:shared` — `commonMain` has neither a locale nor a `String.format`, so a shared version would be
/// hand-rolled anyway.
///
/// **Everything here is integer arithmetic.** A rupee amount is `Int64` minor units on the wire and
/// stays that way to the last character; `Double` is the one thing C012's money fence forbids, and a
/// formatter that divided by 100 to print would reintroduce it at the only place a passenger reads
/// the number.
///
/// The grouping is fixed to `en_US_POSIX` rather than the handset's, because the figure belongs to a
/// Sri Lankan rupee amount: a passenger whose phone groups by lakhs must still read the number the
/// receipt shows.
enum MoneyFormat {

    /// The currency prefix D2' §A prints on every amount.
    ///
    /// **`Rs` is data, not copy** — three identical values in the three `Localizable.strings` files
    /// is exactly what `LocalizationTests` fails on, and a currency prefix is a proper noun. Same
    /// rule `+94` and the language endonyms follow.
    static let prefix = "Rs"

    /// `74000` → `Rs 740`; `48050` → `Rs 480.50`. Whole rupees lose their `.00`.
    static func rupees(_ amountMinor: Int64) -> String {
        let negative = amountMinor < 0
        let absolute = negative ? -amountMinor : amountMinor
        let whole = absolute / minorUnits
        let cents = absolute % minorUnits

        let grouped = grouping.string(from: NSNumber(value: whole)) ?? String(whole)
        let text = cents == 0 ? grouped : grouped + "." + String(format: "%02lld", cents)
        return negative ? "\(prefix) -\(text)" : "\(prefix) \(text)"
    }

    /// The same, for a DTO that carries a `Money` rather than a bare `…Minor` field.
    static func rupees(_ money: Money) -> String { rupees(money.amountMinor) }

    private static let minorUnits: Int64 = 100

    private static let grouping: NumberFormatter = {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.groupingSeparator = ","
        formatter.usesGroupingSeparator = true
        return formatter
    }()
}
