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
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/**
 * SCR-PA-024's state.
 *
 * @property vehicleId What will be asked about. Pre-filled from a marker (AL-23) or typed.
 * @property prefilled Whether the id arrived from the map rather than from the keyboard. Only the
 *   caption differs — the field stays editable either way, because a passenger who opened the
 *   wrong marker should not have to go back to the map to fix a character.
 * @property status Where the decision stands, once there is a request (US-4.6).
 * @property existing The subscription this passenger already has for [vehicleId], when there is
 *   one. Its presence is the only evidence this app has that a request was **accepted** — see
 *   [ModeBRequestViewModel].
 * @property sending Whether the request is in flight.
 */
internal data class ModeBRequestState(
    val vehicleId: String = "",
    val prefilled: Boolean = false,
    val status: AccessRequestStatus? = null,
    val existing: Subscription? = null,
    val loading: Boolean = false,
    val sending: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** Whether **Send request** can fire: an id, nothing in flight, and no grant already held. */
    val canSend: Boolean
        get() = vehicleId.isNotBlank() && !sending && existing == null && status != AccessRequestStatus.PENDING
}

/**
 * SCR-PA-024 — asking a private vehicle's owner for access.
 *
 * **A request is PER VEHICLE, never per fleet** (AL-23). `POST /v1/mode-b/{vehicleId}/access-requests`
 * is scoped to the one vehicle whose marker was tapped, and a passenger who wants to see a second
 * van asks that van's owner separately. The route carries the id optionally for the same reason:
 * from a marker it is known, from the drawer's *"Private transport"* row it is typed.
 *
 * **Nothing is visible until the owner accepts** (D-23). The grant is what fanout-svc checks when
 * a passenger joins a geocell group, so a pending request changes nothing on the map — which is
 * also why AL-25's *"re-joining requires request → accept"* is the same flow as joining the first
 * time, and this screen is deliberately reachable after an unsubscribe.
 *
 * **Accepted is inferred from the subscription, and Rejected cannot be observed at all.** There is
 * no passenger-facing read of one's own access requests — `GET /v1/mode-b/{vehicleId}/access-requests`
 * is the owner's — and notification-svc mints no Mode B push kind, so nothing tells this app a
 * decision was made. What *does* exist is `GET /v1/mode-b/subscriptions/{passengerId}`, and an
 * accepted request creates a subscription in the same transaction: a subscription for this vehicle
 * therefore means accepted, and the screen says so rather than leaving a chip on Pending forever.
 * A rejection leaves no trace either way. Both gaps are recorded in the C082 handoff.
 */
internal class ModeBRequestViewModel(
    vehicleId: Ulid?,
    private val subscriptions: SubscriptionRepository,
    private val sessions: AuthSessionManager,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(
        ModeBRequestState(
            vehicleId = vehicleId.orEmpty(),
            prefilled = !vehicleId.isNullOrBlank(),
        ),
    )

    val state: StateFlow<ModeBRequestState> = mutableState.asStateFlow()

    init {
        loadExisting()
    }

    fun onVehicleIdChange(value: String) {
        // Trimmed, because a Vehicle ID read off a marker and pasted back in brings whitespace
        // with it, and the server's answer to " MR-VEH-48213" is a 404 nobody can act on.
        mutableState.update { it.copy(vehicleId = value.trim(), status = null, error = null) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /** Sends the request. Idempotent on a key minted once, so a double tap is one request (R-14). */
    @Suppress("TooGenericExceptionCaught")
    fun send() {
        val current = mutableState.value
        if (!current.canSend) return

        mutableState.update { it.copy(sending = true, error = null) }
        val key = keys.next()
        viewModelScope.launch {
            try {
                val request = subscriptions.requestAccess(vehicleId = current.vehicleId, idempotencyKey = key)
                mutableState.update { it.copy(sending = false, status = request.status) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(sending = false, error = ModeBErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * Looks for a subscription this passenger already holds for the vehicle in the field.
     *
     * Runs once on entry and after a send, and is silent on failure: it is a courtesy that turns
     * *"Pending approval"* into *"you already have access"*, not a precondition for asking. A
     * passenger who is genuinely already subscribed and taps Send anyway gets the server's
     * `409 conflict`, which `ModeBErrors` has copy for.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun loadExisting() {
        val vehicleId = mutableState.value.vehicleId
        if (vehicleId.isBlank()) return
        val passengerId = (sessions.state.value as? SessionState.SignedIn)?.userId ?: return

        mutableState.update { it.copy(loading = true) }
        viewModelScope.launch {
            try {
                val held = subscriptions.subscriptions(passengerId).items.firstOrNull { it.vehicleId == vehicleId }
                mutableState.update {
                    it.copy(
                        loading = false,
                        existing = held,
                        status = held?.let { _ -> AccessRequestStatus.ACCEPTED } ?: it.status,
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                mutableState.update { it.copy(loading = false) }
            }
        }
    }
}
