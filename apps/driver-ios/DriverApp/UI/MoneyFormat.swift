import Foundation
import MageRideShared

/// Rendering money, distance and a clock for a driver — `Rs 1,240`, `1.2 km`, `01:12:40`.
///
/// **`Money` deliberately does not format itself** (C012): rendering needs a locale and a symbol, and
/// no user-facing string may live in `:shared`. This is the app's half of that split, and it is one
/// type so that the app bar's balance, the offer's fare and the daily-fee chip all group their
/// thousands the same way. The same file is `apps/driver-android/.../ui/MoneyFormat.kt`, function for
/// function.
///
/// **Everything here is integer arithmetic.** A rupee amount is `Long` minor units on the wire and
/// stays that way to the last character — `Double` is the one thing C012's money fence forbids, and a
/// formatter that divided by 100 to print would reintroduce it at the only place a driver reads the
/// number.
///
/// The grouping is fixed to `Locale(identifier: "en_US_POSIX")` rather than the handset's, because
/// the figure belongs to a Sri Lankan rupee amount: a driver who set their phone to a locale that
/// groups by lakhs must still read the same number the receipt shows. Same argument the Android twin
/// makes with `Locale.ROOT`.
enum MoneyFormat {

    /// The currency prefix D2' §A prints on every driver-facing amount.
    ///
    /// **`Rs` is data, not copy** — three identical values in the three `Localizable.strings` files
    /// is exactly what `LocalizationTests` fails on, and a currency prefix is a proper noun. Same
    /// rule `+94` and the language endonyms follow (C086).
    static let prefix = "Rs"

    /// What is drawn where an amount is not known yet — a read in flight, or one that failed.
    ///
    /// An em dash rather than `Rs 0`: zero is a balance a driver can have, and being told they have
    /// it when nothing was read is worse than being told nothing.
    static let empty = MageRideSymbols.unknown

    /// `48000` → `Rs 480`; `48050` → `Rs 480.50`. Whole rupees lose their `.00`.
    static func rupees(_ amountMinor: Int64) -> String {
        let negative = amountMinor < 0
        let absolute = negative ? -amountMinor : amountMinor
        let whole = absolute / minorUnits
        let cents = absolute % minorUnits

        let grouped = grouping.string(from: NSNumber(value: whole)) ?? String(whole)
        let text = cents == 0 ? grouped : grouped + "." + String(cents).leftPadded(to: 2)
        return negative ? "\(prefix) -\(text)" : "\(prefix) \(text)"
    }

    /// The same, for a DTO that carries a `Money` rather than a bare `…Minor` field.
    static func rupees(_ money: Money) -> String { rupees(money.amountMinor) }

    /// `1240.0` metres → `1.2 km`, and anything under a kilometre → `240 m`.
    ///
    /// The tenth of a kilometre is produced by integer arithmetic on rounded metres for the same
    /// reason the rupees are: `String(format: "%.1f")` follows the handset's decimal separator, and a
    /// driver in a comma-decimal locale would read `1,2 km` beside a `Rs 1,240` that means something
    /// else by the comma.
    static func distance(metres: Double) -> String {
        let rounded = Int64(metres.rounded())
        guard rounded >= metresInKm else { return "\(max(rounded, 0)) m" }
        let km = rounded / metresInKm
        let tenths = (rounded % metresInKm) / (metresInKm / 10)
        return "\(km).\(tenths) km"
    }

    /// `01:12:40` — SCR-DI-011's live session timer (US-5.6).
    static func clock(seconds: Int64) -> String {
        let safe = max(seconds, 0)
        return [safe / secondsInHour, (safe % secondsInHour) / secondsInMinute, safe % secondsInMinute]
            .map { String($0).leftPadded(to: 2) }
            .joined(separator: ":")
    }

    /// `1:42` — SCR-DI-013's *"1:42 left"*, hours and minutes only.
    ///
    /// Distinct from ``clock(seconds:)`` on purpose: a Directional activation is measured in hours
    /// and the seconds ticking under it would be a countdown a driver watches instead of the road.
    static func countdown(seconds: Int64) -> String {
        let safe = max(seconds, 0)
        let minutes = (safe % secondsInHour) / secondsInMinute
        return "\(safe / secondsInHour):" + String(minutes).leftPadded(to: 2)
    }

    private static let minorUnits: Int64 = 100
    private static let metresInKm: Int64 = 1_000
    private static let secondsInHour: Int64 = 3_600
    private static let secondsInMinute: Int64 = 60

    /// Thousands separators only — no currency, no decimals, no locale of the handset's choosing.
    private static let grouping: NumberFormatter = {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.maximumFractionDigits = 0
        return formatter
    }()
}

/// The characters that are **symbols rather than sentences**, spelled once.
///
/// Each of these reads the same in Sinhala, Tamil and English, so each of them is a Swift constant
/// rather than three identical entries in the three `Localizable.strings` files — which is precisely
/// what `LocalizationTests.testNoTranslationWasLeftAsItsEnglishPlaceholder` fails on. The same table
/// is `apps/driver-android/.../ui/Symbols.kt`.
enum MageRideSymbols {

    /// *"we have not been told"* — a read in flight, a value the contract has no operation for.
    static let unknown = "—"

    /// The wireframe's `Galle Face → Nugegoda`.
    static let routeArrow = " → "

    /// The `·` every wireframe caption joins its halves with.
    static let separator = " · "
}

extension String {

    /// Left-pads with zeroes, for a clock field and a cent pair.
    func leftPadded(to width: Int) -> String {
        count >= width ? self : String(repeating: "0", count: width - count) + self
    }
}
