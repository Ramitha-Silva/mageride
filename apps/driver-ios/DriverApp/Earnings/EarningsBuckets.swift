import Foundation
import MageRideShared

/// One bar of SCR-DI-020's chart.
///
/// - Parameters:
///   - label: What sits under the bar — an hour (`14`) or a day of the month (`18`). A number, so it
///     is not translated.
///   - netMinor: What the driver kept in this bucket, minor units.
///   - isCurrent: Whether this is the bucket the clock is in. The wireframe highlights one bar; this
///     is which.
struct EarningsBucket: Identifiable, Equatable {

    let label: String
    let netMinor: Int64
    let isCurrent: Bool

    /// Unique within a period by construction — hours run `00`…`23` and days `1`…`31`, and a window
    /// is never longer than one of those.
    var id: String { label }

    /// The bar's magnitude, for a chart axis.
    ///
    /// **The one place a rupee amount becomes a `Double` in this app, and it is not money any more
    /// when it does** — it is a bar height. `MoneyFormat` still never sees it: nothing formats this
    /// value, and the breakdown card beside the chart prints `netMinor` as the integer it is.
    var plottable: Double { Double(netMinor) }
}

/// D2' §SCR-DI-020's *"earnings trend"*, computed from the per-trip rows.
///
/// **The buckets are Asia/Colombo, always** (D-13, D-38). `?period=today` is evaluated in Colombo by
/// query-svc, so a chart bucketed in the handset's zone would draw the server's day across two of its
/// own bars.
///
/// **A day is too coarse for a shift and an hour is too fine for a month**, so the grain follows the
/// period: `EarningsPeriod.today` is bucketed by hour across the hours the driver actually worked,
/// and a week or a month by calendar day across the window the summary reports. Both are derived from
/// the same `GET …/sessions` rows the breakdown list is drawn from — there is no second read and no
/// second arithmetic.
///
/// The same file is `apps/driver-android/.../earnings/EarningsBuckets.kt`, function for function; the
/// calendar is `Foundation.Calendar` where that one is `java.time`, pinned to the same zone through
/// ``ScheduleLabels/zone``.
enum EarningsBuckets {

    /// The bars for [period].
    ///
    /// - Parameters:
    ///   - from: First Colombo day in the window — the summary's `rangeFrom`.
    ///   - to: Last Colombo day in the window — the summary's `rangeTo`.
    ///   - now: The clock, for deciding which bucket is ``EarningsBucket/isCurrent``.
    static func of(
        period: EarningsPeriod,
        sessions: [SessionEarning],
        from: BusinessDate,
        to: BusinessDate,
        now: Date
    ) -> [EarningsBucket] {
        period == EarningsPeriod.today
            ? hourly(sessions: sessions, now: now)
            : daily(sessions: sessions, from: from, to: to, now: now)
    }

    /// One bar per hour worked, from the first trip's hour through the current one.
    ///
    /// A single bar for a whole day would be a total wearing a chart's clothes; the shift's shape is
    /// the only trend a day has. The span always reaches **now**, so an idle afternoon reads as a run
    /// of empty bars rather than as a day that ended at lunchtime.
    private static func hourly(sessions: [SessionEarning], now: Date) -> [EarningsBucket] {
        let calendar = ScheduleLabels.calendar
        let currentHour = calendar.component(.hour, from: now)

        var totals: [Int: Int64] = [:]
        for session in sessions {
            let hour = calendar.component(.hour, from: ScheduleLabels.instant(session.endedAt))
            totals[hour, default: 0] += session.netMinor
        }

        let first = min(totals.keys.min() ?? currentHour, currentHour)
        return (first...currentHour).map { hour in
            EarningsBucket(
                label: String(hour).leftPadded(to: 2),
                netMinor: totals[hour] ?? 0,
                isCurrent: hour == currentHour
            )
        }
    }

    /// One bar per Colombo day in `[from, to]`, with the day the clock is in highlighted.
    private static func daily(
        sessions: [SessionEarning],
        from: BusinessDate,
        to: BusinessDate,
        now: Date
    ) -> [EarningsBucket] {
        let calendar = ScheduleLabels.calendar
        let start = startOfDay(from)
        let end = startOfDay(to)
        guard start <= end else { return [] }

        let today = calendar.startOfDay(for: now)

        var totals: [Date: Int64] = [:]
        for session in sessions {
            let day = calendar.startOfDay(for: ScheduleLabels.instant(session.endedAt))
            totals[day, default: 0] += session.netMinor
        }

        var buckets: [EarningsBucket] = []
        var day = start
        while day <= end {
            buckets.append(
                EarningsBucket(
                    label: String(calendar.component(.day, from: day)),
                    netMinor: totals[day] ?? 0,
                    isCurrent: day == today
                )
            )
            guard let next = calendar.date(byAdding: .day, value: 1, to: day), next > day else { break }
            day = next
        }
        return buckets
    }

    /// Colombo midnight at the start of [date].
    ///
    /// Through `:shared` rather than by reading the `BusinessDate`'s year, month and day: those are
    /// `kotlinx.datetime.LocalDate` properties whose exported spellings have moved between releases
    /// of that library, and `BusinessCalendar.startOfDay` already answers the question with the zone
    /// D-38 fixes. See `IosBusinessDate.kt`.
    private static func startOfDay(_ date: BusinessDate) -> Date {
        Date(timeIntervalSince1970: TimeInterval(IosBusinessDateKt.colomboStartOfDayMillis(date: date)) / 1000)
    }
}
