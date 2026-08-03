package lk.mageride.driver.earnings

import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.SessionEarning

/**
 * query-svc as SCR-DA-020 uses it.
 *
 * **query-svc is the only source of an earning figure, and it is a read model.** Earnings post only
 * from terminal payment states (R-05), so a ride whose payment is still in flight is not on this
 * dashboard — which is why the number here and the fare a driver just watched settle can differ for
 * a few seconds, and why nothing in this app adds up trips of its own to fill the gap.
 *
 * The summary and the per-trip rows are two calls because they are two endpoints, and the summary is
 * **not** derived from the rows: `GET /v1/earnings/{driverId}` is the aggregate query-svc computed,
 * and re-summing a page of sessions would produce a second, quietly different total.
 */
internal class EarningsRepository(private val query: QueryApi) {

    /** `GET /v1/earnings/{driverId}?period=` — gross, fees, penalties and net for the window. */
    suspend fun summary(driverId: Ulid, period: EarningsPeriod): EarningsSummary =
        query.getDriverEarnings(driverId, period)

    /**
     * `GET /v1/earnings/{driverId}/sessions` — the per-trip breakdown (US-8.8).
     *
     * The dates are **Asia/Colombo** business dates (D-13, D-38), which is what the server evaluates
     * `?period=` in as well; passing the summary's own `rangeFrom`/`rangeTo` back is what keeps the
     * card and the rows under it describing the same days.
     */
    suspend fun sessions(driverId: Ulid, from: BusinessDate, to: BusinessDate): List<SessionEarning> =
        query.listEarningSessions(driverId = driverId, from = from, to = to, page = PageRequest.FIRST).items
}
