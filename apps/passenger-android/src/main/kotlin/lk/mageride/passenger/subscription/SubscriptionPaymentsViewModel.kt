package lk.mageride.passenger.subscription

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import lk.mageride.shared.domain.subscription.ModeBBilling

/**
 * SCR-PA-025b's state.
 *
 * @property payments Newest month first. Ordered here rather than trusted from the wire — the
 *   contract fixes no order on `GET …/payments`, and a statement that listed April above June
 *   would read as a missing payment.
 */
internal data class SubscriptionPaymentsState(
    val subscription: Subscription? = null,
    val payments: List<SubscriptionPayment> = emptyList(),
    val loading: Boolean = true,
    @param:StringRes val error: Int? = null,
) {
    /** The vehicle's monthly fare, for the header. `null` on a Free subscription. */
    val fare: Money? get() = subscription?.let(ModeBBilling::fareFor)

    /** Genuinely empty rather than still loading. */
    val empty: Boolean get() = !loading && payments.isEmpty()
}

/**
 * SCR-PA-025b — one subscription's statement (US-23.9).
 *
 * **It is the passenger's half of a ledger the fleet owner also reads.**
 * `GET /v1/mode-b/subscriptions/{id}/payments` and the owner's
 * `GET /v1/mode-b/{vehicleId}/subscribers/{subId}/payments` answer the same rows from the same
 * table — which is why a month the owner has not yet confirmed shows *Pending verification* here
 * rather than nothing at all. A passenger who transferred the money on the 6th and is told they
 * have not paid is the failure this status exists to prevent (US-23.4).
 *
 * **Cash is a row like any other, once it exists.** It appears only after the owner marks it
 * received in the portal (US-23.6) — there is no passenger-side write for it — so an absent cash
 * month means the owner has not recorded it, not that the money was never handed over.
 */
internal class SubscriptionPaymentsViewModel(
    private val subscriptionId: Ulid,
    private val subscriptions: SubscriptionRepository,
    private val sessions: AuthSessionManager,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SubscriptionPaymentsState())

    val state: StateFlow<SubscriptionPaymentsState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch { load() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val page = subscriptions.payments(subscriptionId)
            mutableState.update {
                it.copy(loading = false, payments = page.items.sortedByDescending(SubscriptionPayment::periodMonth))
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(loading = false, error = ModeBErrors.messageFor(cause)) }
            return
        }
        loadSubscription()
    }

    /**
     * Fills in the header's vehicle and fare.
     *
     * A second read, because the statement carries neither: `SubscriptionPayment` has a
     * `subscriptionId` and an amount per row, and the wireframe's header wants the vehicle and the
     * standing monthly fare. Silent on failure — the statement is what the passenger opened, and
     * losing a header is not worth losing it over.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun loadSubscription() {
        val passengerId = (sessions.state.value as? SessionState.SignedIn)?.userId ?: return
        viewModelScope.launch {
            try {
                val subscription = subscriptions.subscriptions(passengerId).items
                    .firstOrNull { it.subscriptionId == subscriptionId }
                mutableState.update { it.copy(subscription = subscription) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // The rows are already drawn; the header stays blank.
            }
        }
    }
}
