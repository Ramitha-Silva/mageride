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
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import lk.mageride.shared.domain.subscription.ModeBBilling
import lk.mageride.shared.domain.subscription.ModeBPaymentRules

/**
 * One card on SCR-PA-025.
 *
 * @property subscription The grant itself.
 * @property fare What this subscriber owes each month — `null` on a **Free** vehicle, which is
 *   `ModeBBilling.fareFor`'s answer and not an interpretation of a missing field.
 * @property monthStatus Where the current month stands, from the latest payment row. `null` while
 *   the statement is still loading, or when it could not be read — the pill is omitted rather than
 *   guessed, because "Paid" shown wrongly is the one error a passenger acts on.
 */
internal data class SubscriptionCard(
    val subscription: Subscription,
    val fare: Money?,
    val monthStatus: SubscriberMonthStatus?,
) {
    /** The wireframe's 💳 Pay and 🧾 rows are **Paid only** — a Free vehicle has no payment UI. */
    val paid: Boolean get() = subscription.billing == SubscriptionBilling.PAID
}

/**
 * SCR-PA-025's state.
 *
 * @property confirming The card whose ✕ was tapped, awaiting the confirm dialog. Unsubscribing is
 *   not undoable in place — see [SubscriptionsViewModel.unsubscribe].
 */
internal data class SubscriptionsState(
    val cards: List<SubscriptionCard> = emptyList(),
    val loading: Boolean = true,
    val confirming: SubscriptionCard? = null,
    val leaving: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {
    /** Genuinely empty rather than still loading — the illustration state. */
    val empty: Boolean get() = !loading && cards.isEmpty()
}

/**
 * SCR-PA-025 — the vehicles this passenger can see, and what they owe for them.
 *
 * **Unsubscribing is one tap and it is not reversible in place** (AL-25, US-23.11). The grant goes,
 * `share.revoked` reaches fanout-svc, the vehicle leaves the map, and coming back means asking the
 * owner again through SCR-PA-024 and waiting for an accept. That is why the ✕ raises a confirm
 * first — the same argument C080 made for the Rs 50 cancellation fee: the moment before the tap is
 * the only moment the passenger can be told what it costs.
 *
 * **The map is cleared here, not by the socket.** `PassengerLiveMap.dropVehicle` erases the marker
 * on the response rather than waiting for the directed `ShareRevoked` to come back; D-22 promises
 * that push in under 200 ms, and this makes the screen honest even when it does not arrive.
 *
 * **The card's Paid/Payment-due pill costs one read per Paid subscription, and there is no cheaper
 * honest answer.** `Subscription` carries `nextDue` and `status` but nothing about the current
 * month: `SubscriberMonthStatus` lives on `SubscriberRow`, which is the **owner's** roster and
 * answers `403 not-owner` here. So the pill comes from the subscription's own statement —
 * `GET …/payments`, latest period — through `ModeBPaymentRules.monthStatus`, which is also what
 * makes *"an online-transfer payment shows Pending verification"* true on this screen and not only
 * on SCR-PA-025b. Deriving it from `nextDue` instead would call an unpaid first month "Paid",
 * because the server advances that date on payment and not on join.
 */
internal class SubscriptionsViewModel(
    private val subscriptions: SubscriptionRepository,
    private val sessions: AuthSessionManager,
    private val live: PassengerLiveMap,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SubscriptionsState())

    val state: StateFlow<SubscriptionsState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    fun refresh() {
        val passengerId = (sessions.state.value as? SessionState.SignedIn)?.userId
        if (passengerId == null) {
            mutableState.update { it.copy(loading = false) }
            return
        }

        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch { load(passengerId) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /** The ✕ was tapped. Nothing has happened yet — [unsubscribe] is what acts. */
    fun confirmUnsubscribe(card: SubscriptionCard) {
        mutableState.update { it.copy(confirming = card) }
    }

    fun dismissConfirm() {
        mutableState.update { it.copy(confirming = null) }
    }

    /**
     * Leaves the vehicle (US-23.11).
     *
     * The row is dropped from the list on success rather than re-read: `GET …/subscriptions`
     * answers a passenger's own grants and a cancelled one is gone from it, so a refetch would
     * cost a round trip to learn what the response already said.
     *
     * The roster row on the **owner's** side survives, muted, until they delete it (US-23.12) —
     * `DELETE /v1/mode-b/{vehicleId}/subscribers/{id}` is theirs, and nothing on this surface can
     * or should reach it.
     */
    @Suppress("TooGenericExceptionCaught")
    fun unsubscribe(card: SubscriptionCard) {
        val subscriptionId = card.subscription.subscriptionId
        mutableState.update { it.copy(confirming = null, leaving = subscriptionId, error = null) }
        val key = keys.next()

        viewModelScope.launch {
            try {
                subscriptions.unsubscribe(subscriptionId, key)
                // D-22's half the client owns. See the class KDoc.
                live.dropVehicle(card.subscription.vehicleId)
                mutableState.update { current ->
                    current.copy(
                        cards = current.cards.filterNot { it.subscription.subscriptionId == subscriptionId },
                        leaving = null,
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(leaving = null, error = ModeBErrors.messageFor(cause)) }
            }
        }
    }

    // ------------------------------------------------------------------------------------------

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load(passengerId: Ulid) {
        try {
            val page = subscriptions.subscriptions(passengerId)
            // Drawn without the pills first: the list is what the passenger came for, and holding
            // it back for N statements would leave the screen empty while they load.
            mutableState.update { current ->
                current.copy(
                    cards = page.items.map { SubscriptionCard(it, ModeBBilling.fareFor(it), monthStatus = null) },
                    loading = false,
                )
            }
            page.items.filter { it.billing == SubscriptionBilling.PAID }.forEach { loadMonthStatus(it) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(loading = false, error = ModeBErrors.messageFor(cause)) }
        }
    }

    /**
     * Fills in one card's pill from its own statement.
     *
     * The latest row is taken by `periodMonth` rather than by list position: the contract fixes no
     * ordering on `GET …/payments`, and "the first item" would be a guess that happens to be right
     * against a server that sorts newest-first.
     *
     * A failure is swallowed — the card keeps its `null` pill, which is what it was already
     * showing, and one unreadable statement must not blank a list of five subscriptions.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun loadMonthStatus(subscription: Subscription) {
        viewModelScope.launch {
            val latest = try {
                subscriptions.payments(subscription.subscriptionId).items.maxByOrNull { it.periodMonth }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                return@launch
            }

            val status = ModeBPaymentRules.monthStatus(latest)
            mutableState.update { current ->
                current.copy(
                    cards = current.cards.map { card ->
                        if (card.subscription.subscriptionId == subscription.subscriptionId) {
                            card.copy(monthStatus = status)
                        } else {
                            card
                        }
                    },
                )
            }
        }
    }
}
