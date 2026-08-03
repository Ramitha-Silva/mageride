package lk.mageride.driver.earnings

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.util.BusinessCalendar
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-020's state.
 *
 * @property period Which tab is selected.
 * @property loading The pair of reads for [period] is in flight — the wireframe's shimmer.
 * @property summary query-svc's own aggregate. **Never recomputed here.**
 * @property trips The per-trip breakdown (US-8.8), newest first.
 * @property buckets The chart.
 * @property error Resolved copy for the last failure.
 */
internal data class EarningsState(
    val period: EarningsPeriod = EarningsPeriod.TODAY,
    val loading: Boolean = true,
    val summary: EarningsSummary? = null,
    val trips: List<SessionEarning> = emptyList(),
    val buckets: List<EarningsBucket> = emptyList(),
    @param:StringRes val error: Int? = null,
) {

    /** The figure the screen leads with — what the driver keeps (US-9.22). */
    val netMinor: Long get() = summary?.netMinor ?: 0L

    /** Fares received, before the platform's cut. */
    val grossMinor: Long get() = summary?.grossMinor ?: 0L

    /** The daily platform fee taken out of the window (US-9.22, D5' §2). */
    val dailyFeeMinor: Long get() = summary?.dailyFeeMinor ?: 0L

    /** Cancellation and no-show penalties netted out (D5' §7.1). */
    val penaltyMinor: Long get() = summary?.penaltyMinor ?: 0L

    /** Gratuities (E-10). */
    val tipMinor: Long get() = summary?.tipMinor ?: 0L

    /** Completed trips in the window. */
    val tripCount: Int get() = summary?.trips ?: 0

    /** D2' §SCR-DA-020's *"empty period"* — the read answered and there was nothing in it. */
    val isEmptyPeriod: Boolean get() = !loading && summary != null && tripCount == 0
}

/**
 * **SCR-DA-020 · the earnings dashboard** (US-9.22, US-8.8).
 *
 * Two reads per tab: `GET /v1/earnings/{driverId}?period=` for the card, and
 * `GET /v1/earnings/{driverId}/sessions` for the rows and the chart.
 *
 * **The summary is the server's arithmetic, and this class does not check it.** Netting the rows
 * ourselves and showing the answer would create a second total that disagrees with the wallet the
 * day a penalty lands between the two reads; `EarningsSummary.netMinor` is what a driver is owed and
 * it is printed as sent. What the rows are for is the breakdown and the trend.
 *
 * **The window is Asia/Colombo and it comes from the server** (D-13, D-38). `rangeFrom`/`rangeTo`
 * are optional on the wire, so the fallback mirrors what query-svc evaluates `?period=` as — today,
 * the seven days ending today, and the calendar month to date — rather than the handset's idea of a
 * week. Near midnight the two disagree, which is the whole reason `BusinessCalendar` exists.
 *
 * ### The payment-method split
 *
 * The deliverable asks for one and **no read on the platform answers it**: `EarningsSummary` and
 * `SessionEarning` carry money and a trip id, `TripSummary` carries no payment method, and
 * fare-svc's payment records are per-payment and are not listable for a driver over a period. It is
 * omitted rather than approximated — a split computed from anything else here would be a guess
 * presented as a reconciliation. Recorded as a spec gap in the C072 handoff; the wireframe's own
 * card does not draw one.
 */
@OptIn(ExperimentalTime::class)
internal class EarningsViewModel(private val identity: DriverIdentity, private val earnings: EarningsRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(EarningsState())

    val state: StateFlow<EarningsState> = mutableState.asStateFlow()

    init {
        load(EarningsPeriod.TODAY)
    }

    /** The Today / Week / Month tabs. Re-reads both endpoints for the new window. */
    fun select(period: EarningsPeriod) {
        load(period)
    }

    /** Re-reads the selected window. */
    fun refresh() {
        load(mutableState.value.period)
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    private fun load(period: EarningsPeriod) {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(period = period, loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val summary = earnings.summary(driverId, period)
            val now = Clock.System.now()
            val today = BusinessCalendar.businessDate(now)
            val from = summary.rangeFrom ?: defaultFrom(period, today)
            val to = summary.rangeTo ?: today

            val sessions = earnings.sessions(driverId, from, to)

            mutableState.update {
                it.copy(
                    loading = false,
                    summary = summary,
                    trips = sessions.sortedByDescending { session -> session.endedAt },
                    buckets = EarningsBuckets.of(
                        period = period,
                        sessions = sessions,
                        from = from,
                        to = to,
                        now = now,
                    ),
                )
            }
        }
    }

    /**
     * The window to ask for when the summary did not name one.
     *
     * `query.yaml` marks `rangeFrom`/`rangeTo` optional, so a response without them is legal. These
     * are the same windows query-svc evaluates `?period=` over — a rolling week rather than a
     * Monday-anchored one, and the calendar month to date — so the rows under the card describe the
     * days the card counted.
     */
    private fun defaultFrom(period: EarningsPeriod, today: BusinessDate): BusinessDate = when (period) {
        EarningsPeriod.TODAY -> today
        EarningsPeriod.WEEK -> BusinessCalendar.plusDays(today, -(DAYS_IN_WEEK - 1))
        EarningsPeriod.MONTH -> BusinessCalendar.firstOfMonth(today)
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    private companion object {
        const val DAYS_IN_WEEK = 7
    }
}
