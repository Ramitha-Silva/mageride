package lk.mageride.driver.vehicle

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import lk.mageride.shared.data.models.Ulid

/**
 * Which vehicle the onboarding screens are talking about.
 *
 * `DriverRoute.VehicleOnboardingStatus` carries **no arguments** — the shell fixed the route table
 * before any screen group existed (C067) — so "show the verdicts for the vehicle I just
 * submitted" cannot be expressed as a navigation argument. It is expressed here instead: the
 * wizard [open]s the vehicle before navigating, My Vehicles does the same when a row is tapped,
 * and SCR-DA-006 reads [vehicleId].
 *
 * Exactly the shape and the justification of `DocumentCaptureCoordinator`, and a process-wide
 * single instance for the same reason: the screen that set the value is not composed while the
 * screen that reads it is on top.
 */
internal class VehicleOnboardingSession {

    private val mutableVehicleId = MutableStateFlow<Ulid?>(null)

    /** The vehicle SCR-DA-006 should render, or `null` when nothing has named one. */
    val vehicleId: StateFlow<Ulid?> = mutableVehicleId.asStateFlow()

    /** Names the vehicle the next visit to SCR-DA-006 is about. Call before navigating. */
    fun open(vehicleId: Ulid) {
        mutableVehicleId.value = vehicleId
    }

    /**
     * Forgets it.
     *
     * Called when a vehicle is deactivated: a status screen restored onto a deleted vehicle would
     * ask registry-svc for it and render a `404` as an error the driver cannot act on.
     */
    fun close() {
        mutableVehicleId.value = null
    }
}
