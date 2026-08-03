package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.wallet.WalletTransaction

/**
 * SCR-DA-025's state.
 *
 * @property filter Which of the four chips is on.
 * @property from Start of the Colombo date range, or `null` for "everything the server sends".
 * @property to End of that range.
 * @property lines Every row the server answered with, unfiltered — the chip is applied on read.
 * @property loading The read is in flight.
 * @property exporting Which format is being downloaded, or `null`.
 * @property exported The format of the last successful download, for the confirmation.
 * @property error Resolved copy for the last failure.
 */
internal data class WalletHistoryState(
    val filter: HistoryFilter = HistoryFilter.ALL,
    val from: BusinessDate? = null,
    val to: BusinessDate? = null,
    val lines: List<WalletTransaction> = emptyList(),
    val loading: Boolean = true,
    val exporting: StatementFormat? = null,
    val exported: StatementFormat? = null,
    @param:StringRes val error: Int? = null,
) {

    /** What the list actually draws. */
    val visible: List<WalletTransaction> get() = lines.filter(filter::keeps)

    /** D2' §SCR-DA-025's *"empty"* — the read answered and the chip kept nothing. */
    val isEmpty: Boolean get() = !loading && visible.isEmpty()

    /** Whether a date range has been chosen, which is what the app bar's chip reflects. */
    val hasRange: Boolean get() = from != null || to != null
}

/**
 * **SCR-DA-025 · payment & fee history** (US-9A.6, US-9A.19, D-13).
 *
 * The wallet ledger, newest first, over a **Colombo** date range (D-13, D-38) — the same range the
 * statement download uses, so what is on screen and what is in the file are the same rows.
 *
 * ### Two filters, and they are not the same kind of thing
 *
 * The **date range** is the server's: `GET /v1/wallet/{userId}/transactions` takes `from` and `to`
 * and evaluates them as Colombo business dates. The four **chips** are the device's, because that
 * route takes no `kind` parameter — adding one would be a contract change, and four chips over the
 * page already read is what the wireframe draws anyway. Filtering locally is also why [lines] holds
 * everything and [WalletHistoryState.visible] is derived: switching chips must not re-hit the API.
 *
 * ### "Receipt download" is the statement
 *
 * wallet-svc declares no per-transaction receipt route. `GET …/transactions` with an `Accept` of
 * `text/csv` or `application/pdf` is the only download in `wallet.yaml`, and US-9A.19 calls it a
 * statement. So the app bar's download offers those two formats over the range on screen, and a
 * row is not separately downloadable. Recorded as a spec gap in the C073 handoff.
 */
internal class WalletHistoryViewModel(
    private val identity: DriverIdentity,
    private val wallet: WalletRepository,
    private val exporter: StatementExporter,
) : ViewModel() {

    private val mutableState = MutableStateFlow(WalletHistoryState())

    val state: StateFlow<WalletHistoryState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** The `All · Fees · Top-ups · Transfers` chips. Local — no read. */
    fun selectFilter(filter: HistoryFilter) {
        mutableState.update { it.copy(filter = filter) }
    }

    /** The date-range filter. Both bounds are **Asia/Colombo** business dates. */
    fun setRange(from: BusinessDate?, to: BusinessDate?) {
        mutableState.update { it.copy(from = from, to = to) }
        refresh()
    }

    /** Re-reads the ledger for the selected range. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        val current = mutableState.value
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val lines = wallet.transactions(driverId, current.from, current.to)
            mutableState.update { it.copy(loading = false, lines = lines) }
        }
    }

    /**
     * US-9A.19's download, in [format], over the range on screen.
     *
     * The chip is deliberately **not** applied: a statement is evidence of what the ledger did, and
     * one that quietly omitted every line the driver had filtered out would be a document that
     * does not reconcile with the balance printed on it.
     */
    fun export(format: StatementFormat) {
        val driverId = identity.driverId ?: return
        val current = mutableState.value
        if (current.exporting != null) return

        mutableState.update { it.copy(exporting = format, exported = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(exporting = null) } }) {
            val bytes = wallet.statement(driverId, format, current.from, current.to)
            val shared = exporter.share(fileName(current, format), format, bytes)
            mutableState.update {
                it.copy(
                    exporting = null,
                    exported = format.takeIf { _ -> shared },
                    error = if (shared) null else R.string.wallet_statement_failed,
                )
            }
        }
    }

    /** Clears the download confirmation. */
    fun dismissExported() {
        mutableState.update { it.copy(exported = null) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * `mageride-wallet-2026-06-01-2026-06-30.csv`.
     *
     * A file name is **data, not copy** — the same rule `Rs` and `+94` follow (C068) — so it is
     * built here from ISO dates rather than from three `strings.xml` entries that would be
     * byte-identical and fail `StringResourceTest`. `all` stands in for an open bound so two
     * downloads of different ranges never overwrite each other in the cache.
     */
    private fun fileName(state: WalletHistoryState, format: StatementFormat): String {
        val from = state.from?.toString() ?: OPEN_BOUND
        val to = state.to?.toString() ?: OPEN_BOUND
        return "$FILE_PREFIX-$from-$to.${format.extension}"
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
        const val FILE_PREFIX = "mageride-wallet"
        const val OPEN_BOUND = "all"
    }
}
