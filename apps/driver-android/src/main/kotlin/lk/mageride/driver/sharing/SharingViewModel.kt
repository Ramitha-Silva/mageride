package lk.mageride.driver.sharing

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
import lk.mageride.driver.ui.PlatformId
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.GrantStatus
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.data.models.subscription.AccessRequest

/**
 * SCR-DA-028's state.
 *
 * @property vehicles The shareable vehicles — Mode A and Mode B only; see [SharingViewModel].
 * @property selectedVehicleId Which vehicle everything below is scoped to.
 * @property userId The passenger id typed into the share form.
 * @property expiresAt When the grant should lapse; `null` is open-ended (US-4.2).
 * @property requests Incoming Mode B access requests **for the selected vehicle** (US-4.4).
 * @property grantees Who can currently see it (US-4.7).
 * @property loading The vehicle read is in flight.
 * @property listsLoading The per-vehicle lists are being re-read after a selector change.
 * @property granting The grant POST is in flight.
 * @property busyRequestId The request whose accept or reject is in flight.
 * @property granted The id of the passenger the last successful grant was offered to.
 * @property error Resolved copy for the last failure.
 */
internal data class SharingState(
    val vehicles: List<VehicleSummary> = emptyList(),
    val selectedVehicleId: Ulid? = null,
    val userId: String = "",
    val expiresAt: Timestamp? = null,
    val requests: List<AccessRequest> = emptyList(),
    val grantees: List<Subscriber> = emptyList(),
    val loading: Boolean = true,
    val listsLoading: Boolean = false,
    val granting: Boolean = false,
    val busyRequestId: Ulid? = null,
    val granted: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {

    /** The vehicle the whole screen is about. */
    val selected: VehicleSummary? get() = vehicles.firstOrNull { it.vehicleId == selectedVehicleId }

    /** Whether what has been typed is a well-formed platform id. Blank is "not yet", not "wrong". */
    val userIdValid: Boolean get() = PlatformId.isValid(userId)

    /** Whether the field should be drawn in `error` — typed something, and it is not an id. */
    val userIdRejected: Boolean get() = userId.isNotBlank() && !userIdValid

    /** Whether **Grant access** is live. */
    val canGrant: Boolean get() = !granting && userIdValid && selectedVehicleId != null

    /** Whether the driver has no vehicle that can be shared at all. */
    val hasNoShareableVehicle: Boolean get() = !loading && vehicles.isEmpty()
}

/**
 * **SCR-DA-028 · sharing management, per vehicle** (US-4.1–4.4, US-4.7/4.8, US-13.9, AL-35).
 *
 * **AL-35's fence: the caption box is gone and the selector is the scope.** The wireframe used to
 * carry a *"Showing sharing for … temporarily assigned by …"* box above the list; it was removed,
 * and the full-device-width vehicle chips took its job — *"the selected chip already conveys the
 * active vehicle"*. So changing the selection is not a filter over one list, it is a **re-read**:
 * [selectVehicle] clears both lists and fetches that vehicle's own, because both endpoints are
 * scoped by `vehicleId` in the path and there is no combined read to filter.
 *
 * **Only Mode A and Mode B vehicles are offered.** `POST /v1/vehicles/{id}/share` is documented as
 * *"offer a passenger access to a **Mode A/B** vehicle"* and the request queue is literally
 * `/v1/mode-b/…`; a Mode C standby tuk has no subscribers and nothing to share. A driver with only
 * Mode C vehicles therefore sees the empty state rather than a selector that grants nothing.
 *
 * **A new grant does not appear in the grantee list, and that is correct.** US-4.3b: visibility
 * begins when the *passenger accepts*, not when the owner offers. The screen acknowledges the offer
 * and leaves the list alone — a row that claimed someone could already see the vehicle would be
 * wrong for as long as they had not answered.
 */
internal class SharingViewModel(private val identity: DriverIdentity, private val sharing: SharingRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(SharingState())

    val state: StateFlow<SharingState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads the shareable vehicles and then the selected one's two lists. */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val all = identity.liveVehicle()
            val shareable = all.vehicles.filter { it.mode == ServiceMode.A || it.mode == ServiceMode.B }
            val selected = mutableState.value.selectedVehicleId
                ?.takeIf { held -> shareable.any { it.vehicleId == held } }
                // The live vehicle first when it is shareable — a driver assigned a fleet van is
                // looking at that van's requests, not at the other vehicle they happen to own.
                ?: all.live?.vehicleId?.takeIf { live -> shareable.any { it.vehicleId == live } }
                ?: shareable.firstOrNull()?.vehicleId

            mutableState.update {
                it.copy(loading = false, vehicles = shareable, selectedVehicleId = selected)
            }
            selected?.let { readListsFor(it) }
        }
    }

    /** The wireframe's full-width vehicle chips. Re-scopes the queue and the grantee list. */
    fun selectVehicle(vehicleId: Ulid) {
        if (mutableState.value.selectedVehicleId == vehicleId) return

        mutableState.update {
            it.copy(
                selectedVehicleId = vehicleId,
                // Emptied rather than left up: a request row belongs to the vehicle it targets, and
                // showing the previous vehicle's queue under a new chip is the one thing AL-35's
                // "never mixed across vehicles" forbids.
                requests = emptyList(),
                grantees = emptyList(),
                listsLoading = true,
                granted = null,
                error = null,
            )
        }
        launchGuarded(onFailure = { mutableState.update { it.copy(listsLoading = false) } }) {
            readListsFor(vehicleId)
        }
    }

    /** The **Share with User ID** field. */
    fun onUserIdChange(raw: String) {
        mutableState.update { it.copy(userId = raw, granted = null, error = null) }
    }

    /** The **Expiry** picker. `null` clears it back to an open-ended grant. */
    fun onExpiryChange(expiresAt: Timestamp?) {
        mutableState.update { it.copy(expiresAt = expiresAt, error = null) }
    }

    /** **Grant access** — `POST /v1/vehicles/{vehicleId}/share` (US-4.1/4.2). */
    fun grant() {
        val current = mutableState.value
        if (!current.canGrant) return
        val vehicleId = current.selectedVehicleId ?: return
        val userId = PlatformId.of(current.userId)

        mutableState.update { it.copy(granting = true, granted = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(granting = false) } }) {
            sharing.grant(vehicleId = vehicleId, userId = userId, expiresAt = current.expiresAt)
            mutableState.update {
                // The form clears because the offer is out; leaving the id in place invites a
                // second grant to the same passenger, which is a second pending invitation.
                it.copy(granting = false, granted = userId, userId = "", expiresAt = null)
            }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * The wireframe's **Accept** / **Reject** pair — one decision with two answers (US-4.4).
     *
     * One function rather than two, because the two differ only in which subscription-svc route is
     * called and share every other rule: one decision at a time, a re-read of both lists after it,
     * and a failed decision that leaves the row where it was so the driver can look at it again.
     *
     * The re-read is what keeps the lists honest. Accepting moves a row out of the queue **and**
     * into the grantee roster — the accept creates the entitlement and starts the subscription in
     * one transaction — and rejecting moves it out of the queue only; moving rows locally would be
     * this screen guessing at what two services just did.
     *
     * @param accept `true` admits them, `false` declines.
     */
    fun decide(requestId: Ulid, accept: Boolean) {
        val vehicleId = mutableState.value.selectedVehicleId ?: return
        if (mutableState.value.busyRequestId != null) return

        mutableState.update { it.copy(busyRequestId = requestId, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(busyRequestId = null) } }) {
            if (accept) sharing.accept(requestId) else sharing.reject(requestId)
            readListsFor(vehicleId)
            mutableState.update { it.copy(busyRequestId = null) }
        }
    }

    /**
     * The two per-vehicle reads, folded in only if the selection has not moved on.
     *
     * A driver tapping quickly between two vehicles has two reads in flight; without the guard the
     * slower one wins and paints the wrong vehicle's queue under the selected chip.
     */
    private suspend fun readListsFor(vehicleId: Ulid) {
        val requests = sharing.requests(vehicleId).filter { it.status == AccessRequestStatus.PENDING }
        val grantees = sharing.grantees(vehicleId).filter { it.status == GrantStatus.ACTIVE }

        mutableState.update {
            if (it.selectedVehicleId != vehicleId) {
                it
            } else {
                it.copy(requests = requests, grantees = grantees, listsLoading = false)
            }
        }
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
}
