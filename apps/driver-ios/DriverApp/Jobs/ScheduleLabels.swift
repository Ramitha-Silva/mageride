import Foundation
import MageRideShared

/// Which calendar day a scheduled pickup falls on, relative to now.
///
/// ``today`` and ``tomorrow`` are **copy** and are resolved from `Localizable.strings` by the
/// screen; ``on(_:)`` is **data** — a rendered date, whose month abbreviation comes from the
/// platform's own locale data rather than from three hand-written translations. Splitting the type
/// this way is what keeps `LocalizationTests`' rule intact: a value identical in all three files is
/// a symbol, and a sentence is not. Same shape as `apps/driver-android/.../jobs/ScheduleLabels.kt`'s
/// `DayLabel`.
enum DayLabel: Equatable {

    /// The pickup is on the current Colombo day.
    case today

    /// The next Colombo day. The wireframe's *"Tomorrow · 11.2 km · Sedan"*.
    case tomorrow

    /// Any other day, already rendered — `18 Jun`, in the driver's language.
    case on(String)
}

/// The clock and calendar SCR-DI-017, SCR-DI-018 and SCR-DI-020 print, in **Asia/Colombo**.
///
/// **Every pickup time is read in Colombo, never in the handset's zone** (D-38). A driver whose
/// phone came back from a trip abroad still on its old zone would otherwise be shown a board sorted
/// correctly and labelled five and a half hours wrong, and would miss the T-30 window on a ride
/// whose card said tomorrow.
///
/// The wall-clock format is fixed 24-hour (`14:00`) because that is what the wireframe prints and
/// because a clock reading is a number, not a sentence — the same rule `Rs` and `+94` follow.
/// `en_US_POSIX` is what pins it: a `DateFormatter` on the driver's own locale would print `2:00 PM`
/// for a handset set to a 12-hour region, and `.timeStyle = .short` follows the *system* 24-hour
/// switch, which is a setting rather than a format. The month abbreviation is the one exception and
/// is genuinely locale data — iOS carries Sinhala and Tamil month names, and hand-writing them into
/// `Localizable.strings` would be re-typing ICU.
///
/// The same file is `apps/driver-android/.../jobs/ScheduleLabels.kt`, function for function.
enum ScheduleLabels {

    /// `Asia/Colombo`, resolved from `:shared`'s calendar so there is one definition of the zone.
    ///
    /// The fallback is UTC and is unreachable — the identifier comes from the tz database entry
    /// `BusinessCalendar.ZONE` was built from — but a `TimeZone(identifier:)` is optional and a
    /// force-unwrap in a static initialiser is a launch crash rather than a wrong label.
    static let zone: TimeZone = TimeZone(identifier: IosBusinessDateKt.colomboZoneId())
        ?? TimeZone(secondsFromGMT: 0)
        ?? .current

    /// A Gregorian calendar pinned to ``zone`` — what every day comparison and hour bucket is made
    /// against. Deliberately not `Calendar.current`: that one carries the handset's zone, and its
    /// `identifier` can be a non-Gregorian calendar in some regions, which would make
    /// *"which day of the month is this"* answer a different number from the one the passenger booked.
    static var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        return calendar
    }

    /// What separates a pickup from its drop on a card, and what a card shows where the server sent
    /// no address. Both are ``MageRideSymbols``' — see that type on why a symbol is not copy.
    static let routeArrow = MageRideSymbols.routeArrow
    static let unknown = MageRideSymbols.unknown

    /// `08:30` — the pickup's Colombo wall-clock time.
    static func time(_ at: Timestamp) -> String { timeFormatter.string(from: instant(at)) }

    /// `17 Jun` — the Colombo calendar date [at] falls on, always rendered.
    ///
    /// ``day(_:now:)`` is for a list where *"Today"* and *"Tomorrow"* carry information; a ledger is
    /// not one — every line is in the past, and a history that said *"Today"* on six rows would have
    /// hidden which of them came first.
    static func date(_ at: Timestamp) -> String { dayFormatter.string(from: instant(at)) }

    /// Which day [at] falls on, seen from [now].
    ///
    /// Compared as **Colombo dates** rather than as a duration: a pickup nine hours away is
    /// "tomorrow" at 22:00 and "today" at 06:00, and only the calendar knows which.
    static func day(_ at: Timestamp, now: Timestamp) -> DayLabel {
        let calendar = self.calendar
        let pickup = calendar.startOfDay(for: instant(at))
        let today = calendar.startOfDay(for: instant(now))

        switch calendar.dateComponents([.day], from: today, to: pickup).day {
        case 0: return .today
        case 1: return .tomorrow
        default: return .on(date(at))
        }
    }

    /// `Maharagama → Fort`.
    ///
    /// `Place.address` is server-supplied display text and is **absent on every scheduled ride**:
    /// `POST /v1/rides/schedule` takes bare coordinates, so dispatch-svc has no address to echo
    /// (`PlaceResponse`'s own remark). The dash is what a card shows rather than a pair of decimal
    /// degrees no driver can read — recorded as a spec gap in the C072 handoff and carried forward.
    static func route(pickup: Place, dropoff: Place) -> String {
        (pickup.address ?? unknown) + routeArrow + (dropoff.address ?? unknown)
    }

    /// A `Timestamp` as a `Date`, through `:shared`'s own reader so no call site spells the
    /// milliseconds conversion twice.
    static func instant(_ at: Timestamp) -> Date {
        Date(timeIntervalSince1970: TimeInterval(IosInstantKt.timestampEpochMillis(instant: at)) / 1000)
    }

    /// Fixed 24-hour, in the invariant locale. See this type's own note.
    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = zone
        formatter.dateFormat = "HH:mm"
        return formatter
    }()

    /// `18 Jun`. Localised, because a month name is one of the few things ICU knows and we do not.
    ///
    /// `DriverLocale.locale` rather than `Locale.current`: D-26 makes the language a *user*
    /// preference, and until the next launch `Locale.current` is still the handset's.
    private static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = DriverLocale.locale
        formatter.timeZone = zone
        formatter.setLocalizedDateFormatFromTemplate("d MMM")
        return formatter
    }()
}
