package lk.mageride.driver.vehicle

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.VehicleSummary

/**
 * One row of My Vehicles.
 *
 * @property summary The vehicle as `GET /v1/vehicles/mine` returned it.
 * @property nextStep Where **Resume** opens, for a vehicle that is still Incomplete (AL-30). Read
 *   per vehicle from `GET …/onboarding-status`, because the list read does not carry it and the
 *   wireframe prints it — *"Incomplete · Step 3 of 4"*, not just "Incomplete".
 */
internal data class VehicleRow(val summary: VehicleSummary, val nextStep: OnboardingStep? = null) {

    /** The vehicle. */
    val vehicleId: Ulid get() = summary.vehicleId

    /** Whether this one can be selected to go live (US-9.6). */
    val canGoLive: Boolean get() = summary.canGoLive

    /** Whether the wizard still has work to do on it. */
    val isIncomplete: Boolean
        get() = summary.isOnboardable && summary.onboardingStatus == OnboardingStatus.INCOMPLETE

    /** 1-based position of [nextStep] in the wizard, for *"Step 3 of 4"*. */
    val nextStepNumber: Int? get() = nextStep?.let { it.ordinal + 1 }
}

/**
 * SCR-DA-026 / 026a's state.
 *
 * @property owned The driver's own Mode C vehicles — the *"My vehicles"* group.
 * @property assigned Fleet vehicles temporarily assigned or shared to them (US-13.9) — the
 *   *"Temporarily assigned to me"* group.
 * @property activeVehicleId Which one is the single live publisher (D-03).
 * @property loading A read is in flight.
 * @property error Resolved copy for the last failure.
 * @property onboardPromptVisible SCR-DA-026a — the *"Onboard a standby vehicle?"* dialog.
 * @property deactivating The vehicle whose deactivate-confirm dialog is up (US-2.16).
 */
internal data class VehiclesState(
    val owned: List<VehicleRow> = emptyList(),
    val assigned: List<VehicleRow> = emptyList(),
    val activeVehicleId: Ulid? = null,
    val loading: Boolean = true,
    @param:StringRes val error: Int? = null,
    val onboardPromptVisible: Boolean = false,
    val deactivating: VehicleRow? = null,
) {
    /** Whether the driver has no vehicle at all — what raises SCR-DA-026a. */
    val isEmpty: Boolean get() = !loading && owned.isEmpty() && assigned.isEmpty()

    /**
     * Whether **any** vehicle could be selected to go live (US-9.6).
     *
     * C070's dashboard asks the same question of the same rule; it is here because this is the
     * screen that can do something about the answer.
     */
    val canGoOnline: Boolean get() = (owned + assigned).any { it.canGoLive }
}

/**
 * **SCR-DA-026 · vehicle management** and **SCR-DA-026a · the no-vehicles popup**.
 *
 * Three rules, all of them AL-30's or US-9.6's rather than this screen's:
 *
 * * **Only an approved Mode C vehicle can be selected to go live** — or a shared/assigned Mode A/B
 *   one, which the Fleet Portal approved. [VehicleSummary.canGoLive] is that rule, once.
 * * **An Incomplete vehicle resumes at its next step, never Step 1.** The ＋ button and a row's
 *   Resume are the same action for that reason: both open the wizard, and
 *   [VehicleOnboardingRepository.resume] is what decides whether that is a resume or a **new**
 *   vehicle at Step 1/4 (US-2.27).
 * * **Owned is Mode C; temporarily assigned is Mode A/B.** The driver app onboards Mode C only
 *   (AL-27), so a Mode A/B vehicle in this driver's list arrived by assignment or share — there is
 *   no other way for one to be there.
 */
internal class VehiclesViewModel(
    private val vehicles: VehicleOnboardingRepository,
    private val session: VehicleOnboardingSession,
    private val activeVehicle: ActiveVehicleStore,
) : ViewModel() {

    private val mutableState = MutableStateFlow(VehiclesState(activeVehicleId = activeVehicle.activeVehicleId))

    val state: StateFlow<VehiclesState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads `GET /v1/vehicles/mine`, and the resume point of anything still Incomplete. */
    @Suppress("TooGenericExceptionCaught")
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            try {
                val rows = vehicles.myVehicles().map { summary ->
                    val row = VehicleRow(summary)
                    // Only for a vehicle that is actually part-way through: one extra read for the
                    // one row that prints a step number, rather than a read per vehicle.
                    if (row.isIncomplete) row.copy(nextStep = nextStepOf(summary.vehicleId)) else row
                }
                val (owned, assigned) = rows.partition { it.summary.isOnboardable }

                // A vehicle that has been deactivated elsewhere must not stay selected — going
                // online as one the platform has retired is a connection that fails with no way
                // for the driver to see why.
                val stillHeld = activeVehicle.activeVehicleId?.takeIf { id -> rows.any { it.vehicleId == id } }
                activeVehicle.activeVehicleId = stillHeld

                mutableState.update {
                    it.copy(
                        owned = owned,
                        assigned = assigned,
                        activeVehicleId = stillHeld,
                        loading = false,
                        onboardPromptVisible = rows.isEmpty(),
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(loading = false, error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * Picks the single live publisher (D-03).
     *
     * Refused for a vehicle that is not go-live eligible rather than left to the dashboard: the row
     * is not selectable in the wireframe either, and a selection the go-online toggle then rejected
     * would be a state the driver cannot understand.
     */
    fun select(row: VehicleRow) {
        if (!row.canGoLive) return
        activeVehicle.activeVehicleId = row.vehicleId
        mutableState.update { it.copy(activeVehicleId = row.vehicleId) }
    }

    /** Names the vehicle whose status the next screen is about — a row tapped through to SCR-DA-006. */
    fun open(row: VehicleRow) {
        session.open(row.vehicleId)
    }

    /**
     * ***Resume ›*** — the wizard reopens **this** row, at its own next incomplete step.
     *
     * Distinct from [startNewVehicle] below, and that distinction is the point: both used to
     * navigate to the same argument-less route and let the wizard search for something unfinished,
     * so ＋ and *Resume ›* were one action wearing two labels.
     */
    fun resumeOnboarding(row: VehicleRow) {
        session.resumeVehicle(row.vehicleId)
    }

    /**
     * **＋, and SCR-DA-026a's *"Yes, onboard ›"*** — the wizard opens at Step 1/4.
     *
     * US-2.27 as amended: ＋ adds a vehicle and never continues one. Before this it resumed
     * whatever was `INCOMPLETE`, which meant a driver adding their second vehicle landed on Step 2
     * of 4 for their first — and, since `POST /v1/vehicles` **is** step 1, that is where every
     * vehicle abandoned after the first screen leaves the wizard pointing.
     */
    fun startNewVehicle() {
        session.startNewVehicle()
    }

    /** SCR-DA-026a's *"Not now"* — the empty list, with no dialog over it. */
    fun dismissOnboardPrompt() {
        mutableState.update { it.copy(onboardPromptVisible = false) }
    }

    /** US-2.16 — the confirm the wireframe requires before a vehicle is removed. */
    fun confirmDeactivate(row: VehicleRow) {
        mutableState.update { it.copy(deactivating = row) }
    }

    fun cancelDeactivate() {
        mutableState.update { it.copy(deactivating = null) }
    }

    /**
     * `POST /v1/vehicles/{id}/deactivate` — US-2.16.
     *
     * It also **releases the plate** from the D-37 active-set index, which is why the same
     * registration can be onboarded again afterwards.
     */
    @Suppress("TooGenericExceptionCaught")
    fun deactivate() {
        val row = mutableState.value.deactivating ?: return
        mutableState.update { it.copy(deactivating = null, loading = true, error = null) }
        viewModelScope.launch {
            try {
                vehicles.deactivate(row.vehicleId)
                if (session.vehicleId.value == row.vehicleId) session.close()
                if (activeVehicle.activeVehicleId == row.vehicleId) {
                    activeVehicle.activeVehicleId = null
                    mutableState.update { it.copy(activeVehicleId = null) }
                }
                refresh()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(loading = false, error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    private suspend fun nextStepOf(vehicleId: Ulid): OnboardingStep? = runCatching {
        val status = vehicles.onboardingStatus(vehicleId)
        status.nextStep ?: status.steps.firstUnverified()
    }.getOrNull()
}
