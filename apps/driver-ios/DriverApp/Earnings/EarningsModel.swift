import Foundation
import MageRideShared

/// SCR-DI-020's state.
///
/// - Parameters:
///   - period: Which tab is selected.
///   - isLoading: The pair of reads for ``period`` is in flight — the wireframe's shimmer.
///   - summary: query-svc's own aggregate. **Never recomputed here.**
///   - trips: The per-trip breakdown (US-8.8), newest first.
///   - buckets: The chart.
///   - errorKey: Resolved copy for the last failure.
struct EarningsState {

    var period = EarningsPeriod.today
    var isLoading = true
    var summary: EarningsSummary?
    var trips: [SessionEarning] = []
    var buckets: [EarningsBucket] = []
    var errorKey: String?

    /// The figure the screen leads with — what the driver keeps (US-9.22).
    var netMinor: Int64 { summary?.netMinor ?? 0 }

    /// Fares received, before the platform's cut.
    var grossMinor: Int64 { summary?.grossMinor ?? 0 }

    /// The daily platform fee taken out of the window (US-9.22, D5' §2).
    var dailyFeeMinor: Int64 { summary?.dailyFeeMinor.map { $0.int64Value } ?? 0 }

    /// Cancellation and no-show penalties netted out (D5' §7.1).
    var penaltyMinor: Int64 { summary?.penaltyMinor.map { $0.int64Value } ?? 0 }

    /// Gratuities (E-10).
    var tipMinor: Int64 { summary?.tipMinor.map { $0.int64Value } ?? 0 }

    /// Completed trips in the window.
    var tripCount: Int { summary.map { Int($0.trips) } ?? 0 }

    /// D2' §SCR-DI-020's *"empty period"* — the read answered and there was nothing in it.
    var isEmptyPeriod: Bool { !isLoading && summary != nil && tripCount == 0 }
}

/// **SCR-DI-020 · the earnings dashboard** (US-9.22, US-8.8).
///
/// Two reads per tab: `GET /v1/earnings/{driverId}?period=` for the card, and
/// `GET /v1/earnings/{driverId}/sessions` for the rows and the chart.
///
/// **The summary is the server's arithmetic, and this class does not check it.** Netting the rows
/// ourselves and showing the answer would create a second total that disagrees with the wallet the
/// day a penalty lands between the two reads; `EarningsSummary.netMinor` is what a driver is owed and
/// it is printed as sent. What the rows are for is the breakdown and the trend.
///
/// **The window is Asia/Colombo and it comes from the server** (D-13, D-38). `rangeFrom`/`rangeTo`
/// are optional on the wire, so the fallback mirrors what query-svc evaluates `?period=` as — today,
/// the seven days ending today, and the calendar month to date — rather than the handset's idea of a
/// week. Near midnight the two disagree, which is the whole reason `BusinessCalendar` exists.
///
/// ### The payment-method split
///
/// The deliverable asks for one and **no read on the platform answers it**: `EarningsSummary` and
/// `SessionEarning` carry money and a trip id, `TripSummary` carries no payment method, and fare-svc's
/// payment records are per-payment and are not listable for a driver over a period. It is omitted
/// rather than approximated — a split computed from anything else here would be a guess presented as
/// a reconciliation. The C072 handoff's spec gap, carried forward; the wireframe's own card does not
/// draw one either.
@MainActor
final class EarningsModel: ObservableObject {

    @Published private(set) var state = EarningsState()

    private let identity: DriverIdentity
    private let earnings: EarningsRepository
    private let now: () -> Date

    /// - Parameter now: The clock the chart's *"which bucket am I in"* is decided against. Injected
    ///   for the reason ``JobBoardModel``'s is: a bucket highlighted at 23:59 Colombo and one
    ///   highlighted at 00:01 are different bars, and a test cannot wait for midnight.
    init(identity: DriverIdentity, earnings: EarningsRepository, now: @escaping () -> Date = Date.init) {
        self.identity = identity
        self.earnings = earnings
        self.now = now
    }

    /// The Today / Week / Month tabs. Re-reads both endpoints for the new window.
    func select(_ period: EarningsPeriod) async {
        await load(period)
    }

    /// Re-reads the selected window.
    func refresh() async {
        await load(state.period)
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    private func load(_ period: EarningsPeriod) async {
        guard let driverId = identity.driverId else { return }
        state.period = period
        state.isLoading = true
        state.errorKey = nil

        do {
            let summary = try await earnings.summary(driverId: driverId, period: period)
            let today = IosBusinessDateKt.colomboBusinessDateNow()
            let from = summary.rangeFrom ?? EarningsModel.defaultFrom(period: period, today: today)
            let to = summary.rangeTo ?? today

            let sessions = try await earnings.sessions(driverId: driverId, from: from, to: to)
            let at = now()

            state.summary = summary
            state.trips = sessions.sorted {
                IosInstantKt.timestampEpochMillis(instant: $0.endedAt)
                    > IosInstantKt.timestampEpochMillis(instant: $1.endedAt)
            }
            state.buckets = EarningsBuckets.of(
                period: period,
                sessions: sessions,
                from: from,
                to: to,
                now: at
            )
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// The window to ask for when the summary did not name one.
    ///
    /// `query.yaml` marks `rangeFrom`/`rangeTo` optional, so a response without them is legal. These
    /// are the same windows query-svc evaluates `?period=` over — a rolling week rather than a
    /// Monday-anchored one, and the calendar month to date — so the rows under the card describe the
    /// days the card counted.
    private static func defaultFrom(period: EarningsPeriod, today: BusinessDate) -> BusinessDate {
        switch period {
        case EarningsPeriod.week:
            return BusinessCalendar.shared.plusDays(date: today, days: -(daysInWeek - 1))
        case EarningsPeriod.month:
            return BusinessCalendar.shared.firstOfMonth(date: today)
        // `today`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default:
            return today
        }
    }

    private static let daysInWeek: Int32 = 7
}
