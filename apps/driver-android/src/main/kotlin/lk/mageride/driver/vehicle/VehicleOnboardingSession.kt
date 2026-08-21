package lk.mageride.driver.vehicle

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.getAndUpdate
import lk.mageride.shared.data.models.Ulid

/**
 * Why the onboarding screens were opened this time.
 *
 * `DriverRoute.VehicleOnboarding` and `…Status` carry **no arguments** — the shell fixed the route
 * table before any screen group existed (C067) — so neither "show the verdicts for the vehicle I
 * just submitted" nor "this ＋ is a new vehicle, not a resume" can be expressed as a navigation
 * argument. Both are expressed here.
 *
 * Exactly the shape and the justification of `DocumentCaptureCoordinator`, and a process-wide
 * single instance for the same reason: the screen that set the value is not composed while the
 * screen that reads it is on top.
 */
internal class VehicleOnboardingSession {

    private val mutableVehicleId = MutableStateFlow<Ulid?>(null)

    private val mutableIntent = MutableStateFlow<WizardIntent?>(null)

    /** The vehicle SCR-DA-006 should render, or `null` when nothing has named one. */
    val vehicleId: StateFlow<Ulid?> = mutableVehicleId.asStateFlow()

    /** Names the vehicle the next visit to SCR-DA-006 is about. Call before navigating. */
    fun open(vehicleId: Ulid) {
        mutableVehicleId.value = vehicleId
    }

    /**
     * **The ＋ button, and SCR-DA-026a's *"Yes, onboard ›"*** — the wizard opens at Step 1/4.
     *
     * US-2.27 used to make ＋ resume whatever was unfinished and start fresh only once the current
     * vehicle was **Approved**, and `VehicleOnboardingRepository.resume` implemented exactly that.
     * On a handset it reads as a defect rather than a rule: a driver whose one vehicle stalled at
     * insurance taps ＋ to add a second and lands on *"Step 2 of 4 · Insurance"* for the first,
     * with nothing on screen saying which vehicle it is. **＋ means add.** Continuing is what the
     * row's own *Resume ›* is for, and it is one tap away on the same screen.
     */
    fun startNewVehicle() {
        mutableVehicleId.value = null
        mutableIntent.value = WizardIntent.NewVehicle
    }

    /**
     * ***Resume ›*** on a My Vehicles row, and SCR-DA-006's *"Continue"* — the wizard opens on
     * **this** vehicle at its own next incomplete step.
     *
     * Naming it rather than letting the wizard search matters the moment a driver has two
     * unfinished vehicles: `resume()` takes the first `INCOMPLETE` row the list happens to return,
     * so resuming the second one used to open the first.
     */
    fun resumeVehicle(vehicleId: Ulid) {
        mutableVehicleId.value = vehicleId
        mutableIntent.value = WizardIntent.Continue(vehicleId)
    }

    /**
     * Reads the intent and clears it, so it belongs to exactly one visit.
     *
     * Called **once**, when the wizard's view model is constructed, and held by it for that view
     * model's life — a retry after a failed load must reuse the intent it opened with rather than
     * fall back to a search. `null` is the honest answer for an entry that named nothing: the Menu
     * tab's *Vehicle Onboarding* row, and a cold start restored onto the route. Those keep AL-30's
     * "resume at the first non-verified step" behaviour.
     */
    fun consumeIntent(): WizardIntent? = mutableIntent.getAndUpdate { null }

    /**
     * Forgets it.
     *
     * Called when a vehicle is deactivated: a status screen restored onto a deleted vehicle would
     * ask registry-svc for it and render a `404` as an error the driver cannot act on.
     */
    fun close() {
        mutableVehicleId.value = null
        mutableIntent.value = null
    }
}

/** What the wizard was opened to do. See [VehicleOnboardingSession.consumeIntent]. */
internal sealed interface WizardIntent {

    /** Step 1/4, whatever else is unfinished. */
    data object NewVehicle : WizardIntent

    /** This vehicle, at its own next incomplete step. */
    data class Continue(val vehicleId: Ulid) : WizardIntent
}
