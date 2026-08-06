package lk.mageride.passenger.history

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.ride.RideErrors
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/** SCR-PA-022's three tabs (US-8.7, US-20.11). */
internal enum class HistoryTab { PAST, SCHEDULED, PACKAGES }

/**
 * SCR-PA-022's state.
 *
 * @property callFor The ride whose **real** number has been fetched, ready for SCR-PA-015a.
 *   `null` until a Call is tapped — see [TripHistoryViewModel.call] for why it is a second read.
 */
internal data class TripHistoryState(
    val tab: HistoryTab = HistoryTab.PAST,
    val rides: List<RideHistoryRow> = emptyList(),
    val packages: List<RideHistoryRow> = emptyList(),
    val scheduled: List<ScheduledRideRow> = emptyList(),
    val loading: Boolean = true,
    val callFor: CallTarget? = null,
    @param:StringRes val error: Int? = null,
) {
    /** What the visible tab shows, so the screen has one list to draw. */
    val visibleRides: List<RideHistoryRow>
        get() = if (tab == HistoryTab.PACKAGES) packages else rides

    /** Whether the tab is genuinely empty rather than still loading — the illustration state. */
    val empty: Boolean
        get() = !loading && when (tab) {
            HistoryTab.SCHEDULED -> scheduled.isEmpty()
            else -> visibleRides.isEmpty()
        }
}

/** A driver, ready to be called. */
internal data class CallTarget(val driverName: String?, val phone: PhoneE164)

/**
 * SCR-PA-022 — Past / Scheduled / Packages.
 *
 * **A completed trip card shows the driver's number and offers a Call** (US-24.4, AL-36) so a
 * passenger can reach the driver about a lost item. **It is withheld for a ride cancelled before
 * assignment** — there was no driver, and inventing a contact for one would be worse than showing
 * none.
 *
 * **The number on the card and the number that gets dialled are not the same value**, and that is a
 * contract gap rather than a design choice. `RideHistoryRow.driver.mobileMasked` is a `PhoneMasked`
 * — `+9477*****67` — and `:shared`'s own KDoc says *"never parse this back into a dialable
 * number"*. AL-48 made the clear number `RideDetail.counterpartyPhone`, which only
 * `GET /v1/rides/{rideId}` carries. So the card renders the masked form and **Call** fetches the
 * ride. `ride.yaml`'s note still claims the row exists *"so the post-trip Call action can be
 * rendered without a second round-trip"*, which was true before AL-48 and is not now. Recorded in
 * the C081 handoff.
 */
internal class TripHistoryViewModel(private val history: HistoryRepository, private val sessions: AuthSessionManager) :
    ViewModel() {

    private val mutableState = MutableStateFlow(TripHistoryState())

    val state: StateFlow<TripHistoryState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    fun select(tab: HistoryTab) {
        mutableState.update { it.copy(tab = tab, error = null) }
        if (tab == HistoryTab.SCHEDULED && mutableState.value.scheduled.isEmpty()) loadScheduled()
    }

    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch { loadRides() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * A Call was tapped — fetch the ride for its clear number, then open SCR-PA-015a.
     *
     * A **cancelled-before-assignment** ride has no `counterpartyPhone` and no driver, so this
     * answers nothing and the card does not offer the action in the first place. Doing the check
     * in both places is deliberate: the card's is what a passenger sees, and this one is what a
     * deep link or a stale list cannot get around.
     */
    @Suppress("TooGenericExceptionCaught")
    fun call(row: RideHistoryRow) {
        if (!row.hasReachableDriver()) return
        viewModelScope.launch {
            try {
                val ride = history.ride(row.rideId)
                val phone = ride.counterpartyPhone
                if (phone == null) {
                    // AL-48's own rule, from the server's side: withheld for rides cancelled
                    // pre-assignment. Nothing to dial and nothing to apologise for.
                    mutableState.update { it.copy(error = null) }
                } else {
                    mutableState.update { it.copy(callFor = CallTarget(ride.driver?.name, phone)) }
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** The chooser has been shown. */
    fun onCallConsumed() {
        mutableState.update { it.copy(callFor = null) }
    }

    // ------------------------------------------------------------------------------------------

    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadRides() {
        try {
            val page = history.rides()
            // The Packages tab is the same history filtered by kind. `RideHistoryRow` carries no
            // kind, so the split is by what a package ride ends as — see `isPackage`.
            mutableState.update {
                it.copy(
                    rides = page.items.filterNot(RideHistoryRow::isPackage),
                    packages = page.items.filter(RideHistoryRow::isPackage),
                    loading = false,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(loading = false, error = RideErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private fun loadScheduled() {
        val userId = (sessions.state.value as? SessionState.SignedIn)?.userId ?: return
        viewModelScope.launch {
            try {
                mutableState.update { it.copy(scheduled = history.scheduled(userId)) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // An empty Scheduled tab either way — see `ApiHistoryRepository.scheduled`.
            }
        }
    }
}

/**
 * Whether this row's driver can be reached (US-24.4, AL-48).
 *
 * **`false` for a ride cancelled before assignment**, which is the Definition-of-Done line
 * *"a cancelled-before-assignment trip shows no driver number"*. There was never a driver; the row
 * carries no `driver` block, and the state says which kind of cancel it was.
 */
internal fun RideHistoryRow.hasReachableDriver(): Boolean =
    driver != null && state != RideState.CancelledByRiderBeforeAccept && state != RideState.ExpiredNoDriver

/**
 * Whether this row belongs on the **Packages** tab.
 *
 * `RideHistoryRow` carries no `kind`, so the only signal is the terminal state a parcel reaches:
 * `CashOnDeliveryCollected` is P-08's, and nothing but a package gets there. A passenger ride and a
 * package paid by card are indistinguishable in this row — recorded in the C081 handoff.
 */
internal fun RideHistoryRow.isPackage(): Boolean = state == RideState.CashOnDeliveryCollected
