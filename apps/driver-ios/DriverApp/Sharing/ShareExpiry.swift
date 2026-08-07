import Foundation
import MageRideShared

/// SCR-DI-028's **Expiry** — the day a share grant lapses, in Colombo.
///
/// **A grant lapses at the END of the chosen day, not at its start.** A driver who picks 30 June means
/// *"through the 30th"*. Sending Colombo midnight would revoke the passenger a whole day early, and
/// US-4.8's auto-revoke is the server acting on exactly this value.
///
/// **Δ iOS — the second hop the Android twin needs does not exist here, and it is worth saying why.**
/// M3's `DatePickerState` answers **UTC midnight** of the day that was tapped, so `ShareExpiry.kt` has
/// to read that instant back as a date in UTC before it can close it in Colombo — two conversions, and
/// the file is mostly a warning about them. A SwiftUI `DatePicker` handed
/// ``ScheduleLabels/calendar`` and ``ScheduleLabels/zone`` in its environment answers an instant
/// **already in the Colombo day the driver tapped**, so there is one hop and it is the one that
/// carries meaning. Same removal ``WalletHistoryScreen``'s date range makes, for the same reason.
enum ShareExpiry {

    /// The last instant of the Colombo day `picked` falls in.
    ///
    /// Built as *"the start of the next day, one millisecond earlier"* rather than by naming
    /// 23:59:59.999: only the calendar knows which day follows a given one, and adding 24 hours would
    /// be wrong across a DST boundary in any zone that has them. Asia/Colombo has none, and writing
    /// the rule the way that stays true anywhere costs nothing.
    static func endOfDay(_ picked: Date) -> Timestamp {
        let calendar = ScheduleLabels.calendar
        let start = calendar.startOfDay(for: picked)
        let next = calendar.date(byAdding: DateComponents(day: 1), to: start) ?? start
        let lapses = next.addingTimeInterval(-Self.oneMillisecond)

        return IosInstantKt.timestampFromEpochMillis(millis: Int64((lapses.timeIntervalSince1970 * 1000).rounded()))
    }

    /// `30 Jun 2026` — an expiry, printed.
    ///
    /// The year is carried where ``ScheduleLabels/date(_:)`` drops it: a wallet line is always in the
    /// past and its year is obvious, while a grant can be set to lapse in a year's time and *"30 Jun"*
    /// would then be genuinely ambiguous. The month abbreviation is ICU's in the driver's language, for
    /// the same reason it is there.
    static func label(_ at: Timestamp) -> String {
        formatter.string(from: ScheduleLabels.instant(at))
    }

    /// The picked day a stored expiry stands for, for re-opening the picker on the date in force.
    static func date(_ at: Timestamp) -> Date {
        ScheduleLabels.calendar.startOfDay(for: ScheduleLabels.instant(at))
    }

    private static let oneMillisecond: TimeInterval = 0.001

    /// `d MMM yyyy` in the driver's language, on the Colombo clock. See ``ScheduleLabels`` on why the
    /// locale is ``DriverLocale/locale`` rather than `Locale.current`.
    private static let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = DriverLocale.locale
        formatter.timeZone = ScheduleLabels.zone
        formatter.setLocalizedDateFormatFromTemplate("d MMM yyyy")
        return formatter
    }()
}
