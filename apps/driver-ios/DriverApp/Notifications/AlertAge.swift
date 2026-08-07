import Foundation

/// How old an alert is, as SCR-DI-034 prints it — *"2 min ago"*, *"1 h ago"*, *"Yesterday"*.
///
/// **String keys rather than `RelativeDateTimeFormatter`.** The platform formatter reads
/// `Locale.current`, and this app's language is a *user* preference D-26 makes: ``DriverLocale``
/// redirects the bundle every lookup goes through, and `Locale.current` stays the handset's until
/// the next launch. A driver who chose සිංහල would get English relative times on the one screen
/// whose every row carries one — for exactly the users this platform is for. The same argument
/// `apps/driver-android/.../notifications/AlertAge.kt` makes about
/// `DateUtils.getRelativeTimeSpanString`.
///
/// - Parameters:
///   - labelKey: Which string to render.
///   - value: Its `%1$d` argument, or `nil` for the two labels that take none.
struct AlertAge: Equatable {

    let labelKey: String
    let value: Int?

    /// The age of an alert received at [receivedAt], as of [now].
    ///
    /// **Elapsed time rather than a calendar comparison**, which is deliberate: *"Yesterday"* here
    /// means *"about a day ago"*, and a notification is not a business date. Nothing on this screen
    /// is compared against a Colombo day boundary, so D-38's rule does not bite — that one is about
    /// `fee_date` and `period_month`, which this is not. `ScheduleLabels` is therefore **not** used
    /// here, and that is a decision rather than an oversight.
    static func of(receivedAt: Date, now: Date) -> AlertAge {
        let elapsed = max(now.timeIntervalSince(receivedAt), 0)
        switch elapsed {
        case ..<minute:
            return AlertAge(labelKey: "alert_age_now", value: nil)
        case ..<hour:
            return AlertAge(labelKey: "alert_age_minutes", value: Int(elapsed / minute))
        case ..<day:
            return AlertAge(labelKey: "alert_age_hours", value: Int(elapsed / hour))
        case ..<(day * 2):
            return AlertAge(labelKey: "alert_age_yesterday", value: nil)
        default:
            return AlertAge(labelKey: "alert_age_days", value: Int(elapsed / day))
        }
    }

    /// This age, rendered in the driver's language.
    var text: String {
        guard let value else { return labelKey.localised }
        return labelKey.localisedFormat(value)
    }

    private static let minute: TimeInterval = 60
    private static let hour: TimeInterval = 60 * 60
    private static let day: TimeInterval = 24 * 60 * 60
}
