package lk.mageride.driver.home

import lk.mageride.driver.vehicle.ActiveVehicleStore
import lk.mageride.driver.vehicle.canGoLive
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/**
 * Which vehicle this handset is Home *for*, and whether Home is SCR-DA-010 or SCR-DA-011.
 *
 * @property vehicles Everything the driver owns or has been assigned.
 * @property live The single active publisher (D-03), or `null` when nothing is eligible.
 */
internal data class LiveVehicle(val vehicles: List<VehicleSummary> = emptyList(), val live: VehicleSummary? = null) {

    /**
     * **US-9.6's gate.** The go-online toggle is disabled until at least one vehicle is available —
     * an owned Mode C vehicle that is APPROVED, or a shared / temporarily-assigned Mode A/B one.
     */
    val canGoOnline: Boolean get() = live != null

    /** Whether the driver has any vehicle at all; `false` is what routes to SCR-DA-026a. */
    val hasAnyVehicle: Boolean get() = vehicles.isNotEmpty()

    /**
     * Whether Home is the Start/End Journey dashboard rather than the Mode C standby map.
     *
     * D2' §SCR-DA-011: *"This screen IS the driver's home dashboard whenever the active vehicle is
     * a public-transport bus (Mode A) or a private-transport vehicle (Mode B)"*. The mode of the
     * **live** vehicle decides it — a driver who owns a tuk and drives a fleet bus sees the bus
     * dashboard while the bus is the one selected, and the tuk's the moment they switch back.
     */
    val isScheduledMode: Boolean get() = live?.mode == ServiceMode.A || live?.mode == ServiceMode.B
}

/**
 * Who the driver is and what they are driving — the two answers every screen in this group needs
 * before it can read anything else.
 *
 * **The driver id is the signed-in user id.** `AuthSessionManager` is the only thing that holds a
 * session (C014) and `SessionState.SignedIn.userId` is what `GET /v1/drivers/{driverId}/level`,
 * `GET /v1/fees/{driverId}/today` and `POST /v1/rides/{rideId}/offer/{driverId}/accept` all take.
 * Nothing here mints or stores an id of its own.
 */
internal class DriverIdentity(
    private val registry: RegistryApi,
    private val sessions: AuthSessionManager,
    private val activeVehicle: ActiveVehicleStore,
) {

    /** The signed-in driver, or `null` when the session has gone (the shell routes to Login). */
    val driverId: Ulid?
        get() = (sessions.state.value as? SessionState.SignedIn)?.userId

    /**
     * Resolves the single live publisher from `GET /v1/vehicles/mine` and the local choice.
     *
     * The stored choice wins while it is still eligible. When it is not — deactivated elsewhere, an
     * assignment that lapsed, or a driver who has never opened My Vehicles — the first eligible
     * vehicle is taken **and written back**, so SCR-DA-026's radio and this dashboard's chip never
     * disagree about which vehicle is live. A driver with two eligible vehicles changes the pick
     * there; picking one here rather than none is what stops a perfectly onboarded driver finding a
     * disabled toggle on their first shift.
     */
    suspend fun liveVehicle(): LiveVehicle {
        val all = registry.listMyVehicles().items
        val eligible = all.filter { it.canGoLive }
        val held = activeVehicle.activeVehicleId?.let { id -> eligible.firstOrNull { it.vehicleId == id } }
        val live = held ?: eligible.firstOrNull()

        activeVehicle.activeVehicleId = live?.vehicleId
        return LiveVehicle(vehicles = all, live = live)
    }
}
