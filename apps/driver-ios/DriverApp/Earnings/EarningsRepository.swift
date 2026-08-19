import Foundation
import MageRideShared

/// query-svc as SCR-DI-020 uses it.
///
/// **query-svc is the only source of an earning figure, and it is a read model.** Earnings post only
/// from terminal payment states (R-05), so a ride whose payment is still in flight is not on this
/// dashboard — which is why the number here and the fare a driver just watched settle can differ for
/// a few seconds, and why nothing in this app adds up trips of its own to fill the gap.
///
/// The summary and the per-trip rows are two calls because they are two endpoints, and the summary is
/// **not** derived from the rows: `GET /v1/earnings/{driverId}` is the aggregate query-svc computed,
/// and re-summing a page of sessions would produce a second, quietly different total.
///
/// A protocol for the reason every seam in this target is one — see ``JobsRepository``.
protocol EarningsRepository: AnyObject {

    /// `GET /v1/earnings/{driverId}?period=` — gross, fees, penalties and net for the window.
    func summary(driverId: String, period: EarningsPeriod) async throws -> EarningsSummary

    /// `GET /v1/earnings/{driverId}/sessions` — the per-trip breakdown (US-8.8).
    func sessions(driverId: String, from: BusinessDate, to: BusinessDate) async throws -> [SessionEarning]
}

/// ``EarningsRepository`` over `:shared`'s typed query client.
final class ApiEarningsRepository: EarningsRepository {

    private let query: QueryApi

    init(query: QueryApi) {
        self.query = query
    }

    func summary(driverId: String, period: EarningsPeriod) async throws -> EarningsSummary {
        try await query.getDriverEarnings(driverId: driverId, period: period)
    }

    /// The dates are **Asia/Colombo** business dates (D-13, D-38), which is what the server evaluates
    /// `?period=` in as well; passing the summary's own `rangeFrom`/`rangeTo` back is what keeps the
    /// card and the rows under it describing the same days.
    ///
    /// **`Page<T>.items` arrives as `[Any]` and the element cast is this app's.** The export emits the
    /// property as `NSArray<id>` — the enclosing `Page<T>` is generic on the bridge and its rows are not
    /// — so the concrete type has to be put back on this side. `compactMap` rather than `as!` for the
    /// reason C091 wrote down: a failed force cast raises an exception Swift cannot catch and the process
    /// terminates. Every row is a `SessionEarning` by contract, so nothing is dropped.
    func sessions(driverId: String, from: BusinessDate, to: BusinessDate) async throws -> [SessionEarning] {
        try await query.listEarningSessions(
            driverId: driverId,
            from: from,
            to: to,
            page: PageRequest.companion.FIRST
        ).items.compactMap { $0 as? SessionEarning }
    }
}
