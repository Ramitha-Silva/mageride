import Foundation
import MageRideShared
// For ``StatusPill/Tone`` alone: ``RideStateLabel`` answers a pill's tone as well as its key, so the
// tone table and the key table stay one function rather than two that can disagree.
import SwiftUI

/// The clock, the calendar and the two derived labels SCR-PI-022 and SCR-PI-023 print, in
/// **Asia/Colombo**.
///
/// **Every time on a history card is read in Colombo, never in the handset's zone** (D-38). A
/// passenger whose phone came back from a trip abroad still on its old zone would otherwise see a
/// list sorted correctly and dated five and a half hours wrong — and *"17 Jun"* on a receipt has to
/// be the day the ride actually happened, because that is the day a dispute is filed about.
///
/// The wall-clock format is fixed 24-hour (`08:32`) because that is what the wireframe prints and
/// because a clock reading is a number rather than a sentence — the same rule `Rs` and `+94` follow.
/// `en_US_POSIX` is what pins it: a `DateFormatter` on the passenger's own locale prints `8:32 AM`
/// for a 12-hour region, and `.timeStyle = .short` follows the *system* 24-hour switch, which is a
/// setting rather than a format. The month abbreviation is the one exception and is genuinely locale
/// data — iOS carries Sinhala and Tamil month names, and hand-writing them into
/// `Localizable.strings` would be re-typing ICU.
///
/// This is `apps/driver-ios/DriverApp/Jobs/ScheduleLabels.swift` with the locale seam changed. The
/// two are deliberately the same shape; if a third caller appears the file belongs in the shared
/// SwiftUI package `apps/passenger-ios/CLAUDE.md` records as deferred.
enum TripLabels {

    /// `Asia/Colombo`, resolved from `:shared`'s business calendar so there is one definition of the
    /// zone on the platform.
    ///
    /// The fallback is UTC and is unreachable — the identifier comes from the tz database entry
    /// `BusinessCalendar.ZONE` was built from — but `TimeZone(identifier:)` is optional and a
    /// force-unwrap in a static initialiser is a launch crash rather than a wrong label.
    static let zone: TimeZone = TimeZone(identifier: IosBusinessDateKt.colomboZoneId())
        ?? TimeZone(secondsFromGMT: 0)
        ?? .current

    /// A Gregorian calendar pinned to ``zone``.
    ///
    /// Deliberately not `Calendar.current`: that one carries the handset's zone, and its `identifier`
    /// can be a non-Gregorian calendar in some regions — which would make *"which day of the month is
    /// this"* answer a different number from the one on the passenger's booking.
    static var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        return calendar
    }

    /// `17 Jun` — the Colombo calendar date the instant falls on.
    static func date(_ at: Timestamp) -> String { dayFormatter.string(from: instant(at)) }

    /// The same, for a `Date` the app already holds.
    static func date(_ at: Date) -> String { dayFormatter.string(from: at) }

    /// `17 Jun, 08:32` — SCR-PI-023's Date row.
    ///
    /// Assembled from the two formatters rather than from a third `dateFormat`, because the comma is
    /// punctuation the wireframe draws and the two halves have different rules: one is locale data
    /// and the other is deliberately invariant.
    static func dateTime(_ at: Timestamp) -> String {
        date(at) + MageRideSymbols.dateTimeSeparator + timeFormatter.string(from: instant(at))
    }

    /// The same, for a `Date`.
    static func dateTime(_ at: Date) -> String {
        date(at) + MageRideSymbols.dateTimeSeparator + timeFormatter.string(from: at)
    }

    /// `Nugegoda → Galle Face`.
    ///
    /// `Place.address` is server-supplied display text and is absent on several reads — every
    /// scheduled ride carries bare coordinates, because `POST /v1/rides/schedule` takes coordinates
    /// and dispatch-svc has no address to echo. The dash is what a card shows rather than a pair of
    /// decimal degrees no passenger can read.
    static func route(pickup: String?, dropoff: String?) -> String {
        (pickup ?? MageRideSymbols.unknown) + MageRideSymbols.routeArrow + (dropoff ?? MageRideSymbols.unknown)
    }

    /// A `Timestamp` as a `Date`, through `:shared`'s own reader so no call site spells the
    /// milliseconds conversion twice — and so this app and the driver app read an instant the same
    /// way.
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

    /// `17 Jun`. Localised, because a month name is one of the few things ICU knows and we do not.
    ///
    /// `PassengerLocale.locale` rather than `Locale.current`: AL-26/D-26 make the language a *user*
    /// preference, and until the next launch `Locale.current` is still the handset's.
    private static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = PassengerLocale.locale
        formatter.timeZone = zone
        formatter.setLocalizedDateFormatFromTemplate("d MMM")
        return formatter
    }()
}

/// What SCR-PI-022's `pill-status` says about a finished ride.
///
/// The wireframe draws three — `Paid` (ok), `Cash` (muted) and `Cancelled` (err) — and `RideState`
/// has nine terminal values, so this is the mapping rather than a fourth spelling of it in the card.
///
/// **The pill is translated copy, not the enum's name.** `apps/passenger-android`'s card prints
/// `row.state.name` — `CashOnDeliveryCollected` on a parcel — which is an untranslated wire value on
/// a screen D2' §A requires in three languages. Recorded in the C099 handoff (Δ C099); the same
/// mapping was added there.
enum RideStateLabel {

    /// The key and the tone for a terminal ride state.
    static func pill(for state: RideState) -> (key: String, tone: StatusPill.Tone) {
        switch state {
        case RideState.paid:
            return ("history_state_paid", .ok)

        case RideState.cashSettled:
            return ("history_state_cash", .muted)

        // P-08's terminal state for a parcel, and the only signal a `RideHistoryRow` carries that it
        // *was* one — see ``RideHistoryRow/isPackage``.
        case RideState.cashOnDeliveryCollected:
            return ("history_state_cod", .muted)

        case RideState.cancelledByRiderBeforeAccept,
             RideState.cancelledByRiderAfterAccept,
             RideState.cancelledByDriver,
             RideState.expiredNoDriver:
            return ("history_state_cancelled", .error)

        case RideState.noShowRider, RideState.noShowDriver:
            return ("history_state_no_show", .error)

        // `Completed` and `PaymentPending` are not terminal and should not reach a history list;
        // ride-svc sends what it sends, and *"Not paid"* is the honest reading of one that does.
        default:
            return ("summary_unpaid", .warning)
        }
    }
}
