package lk.mageride.passenger.home

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.live.LiveStatus
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.live.toMapVehicle
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.location.asPoint
import lk.mageride.passenger.map.MapFix
import lk.mageride.passenger.map.MapVehicle
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.domain.geo.distanceMetres
import lk.mageride.shared.realtime.VehicleFrame

/**
 * What tapping a marker did (AL-23, US-7.4).
 *
 * Three modes, three different answers, and the middle one is the fence: **a Mode B marker does
 * not open the popup.** It is a vehicle the passenger may or may not be entitled to ride, and the
 * question a tap asks is *"may I subscribe?"* — which is SCR-PA-024's, with the vehicle already
 * filled in.
 */
internal sealed interface MarkerTap {

    /** SCR-PA-007. Mode A only — a bus or a train, with its route and ETA. */
    data class ShowPopup(val vehicle: VehicleFrame) : MarkerTap

    /** SCR-PA-024, vehicle id pre-filled (US-4.6). */
    data class RequestModeBAccess(val vehicleId: String) : MarkerTap

    /**
     * Nothing happens.
     *
     * US-7.4: *"Standby on-demand vehicles do not show info when tapped."* An idle Mode C tuk on
     * the map is a vehicle a passenger books through SCR-PA-009, not one they inspect — and its
     * driver's name is not theirs to see until the ride is accepted (US-7.12).
     */
    data object Ignored : MarkerTap
}

/**
 * Why the map has nothing on it (US-7.14).
 *
 * *"an in-app message when no vehicles of my selected type are active in my area, instead of
 * seeing an empty map with no context"* — and the context is which of these it is.
 */
internal enum class EmptyReason {

    /** Something is on the map. */
    NONE,

    /** The filter is off — the passenger did this and can undo it. */
    FILTERED_OUT,

    /** Nothing is nearby. The genuine "no {type} active nearby". */
    NOTHING_NEARBY,

    /** The socket is down, so what is drawn is last-known and possibly nothing (US-15.2). */
    OFFLINE,
}

/**
 * The half of SCR-PA-007 the live plane cannot answer.
 *
 * `VehicleFrame` is a **position** — id, coordinate, heading, type, mode — because that is what
 * fans out to nineteen geocell groups several times a second, and putting a driver's name in it
 * would put a driver's name on every frame of every vehicle. The popup's other three fields come
 * from `GET /v1/nearby`'s `NearbyVehicle`, fetched once, when a marker is actually tapped.
 *
 * @property etaSeconds Seconds **to the querying passenger**, which is why the lookup is centred on
 *   their fix rather than on the vehicle.
 * @property driverName Mode A only here. US-7.12 makes a Mode C driver's name conditional on the
 *   ride being accepted, and this screen has no ride.
 * @property registrationNumber The plate — the wireframe's `NB-4521`.
 */
internal data class VehicleDetail(
    val etaSeconds: Int? = null,
    val driverName: String? = null,
    val registrationNumber: String? = null,
)

/**
 * SCR-PA-010's state.
 *
 * @property vehicles What to draw, already filtered.
 * @property filter SCR-PA-006's answer.
 * @property fix The passenger's own position — MAP-02's circle and §0.3's blue dot.
 * @property status Where the live plane is; drives the reconnect chip and the dimming (SCR-PA-032).
 * @property shortcuts US-7.13's ★ Home / ★ Work chips.
 * @property selected The vehicle SCR-PA-007 is open for, or `null`.
 * @property detail What query-svc knows about [selected] beyond its position — ETA, driver, plate.
 *   `null` while the lookup is in flight, and for a vehicle the snapshot did not come back with.
 * @property recents The wireframe's *"Recent"* rows, from §2.2's local table.
 * @property lastFrames Everything the plane says is visible, **unfiltered**. Held so that toggling
 *   a chip re-draws from what is already in hand — the wireframe calls the filter *"instant
 *   client-side"*, and a re-query would put a network round trip behind a switch.
 */
internal data class LiveMapState(
    val vehicles: List<MapVehicle> = emptyList(),
    val filter: MapFilter = MapFilter(),
    val fix: MapFix? = null,
    val status: LiveStatus = LiveStatus.Disconnected,
    val shortcuts: List<SavedAddress> = emptyList(),
    val selected: VehicleFrame? = null,
    val detail: VehicleDetail? = null,
    val recents: List<GeocodedPlace> = emptyList(),
    val lastFrames: List<VehicleFrame> = emptyList(),
) {
    /** Whether what is drawn is last-known rather than live — SCR-PA-032's dimmed map. */
    val stale: Boolean get() = status != LiveStatus.Connected

    /** US-7.14's message, or [EmptyReason.NONE]. */
    val emptyReason: EmptyReason
        get() = when {
            vehicles.isNotEmpty() -> EmptyReason.NONE
            stale -> EmptyReason.OFFLINE
            filter.showsNothing -> EmptyReason.FILTERED_OUT
            else -> EmptyReason.NOTHING_NEARBY
        }
}

/**
 * SCR-PA-010 — the live map.
 *
 * **This screen owns the R-06 subscription's input and nothing else about it.** The socket, the
 * nineteen cells, the 30 s hysteresis and the reconnect are `PassengerLiveMap`'s (C076); what the
 * map does is feed it a position and read back what to draw. That split is why the cell arithmetic
 * is tested with no server and this class is tested with no map.
 *
 * **The filter is applied here rather than in the plane.** The plane holds what the *platform* says
 * is visible — entitlement, freshness, engagement — and the filter holds what the *passenger* asked
 * to see. Folding the two would make a Mode B vehicle the passenger has no grant for
 * indistinguishable from one they simply switched off.
 */
internal class LiveMapViewModel(
    private val live: PassengerLiveMap,
    private val locations: PassengerLocationSource,
    private val iam: IamApi,
    private val query: QueryApi,
    private val recents: RecentPlaces,
) : ViewModel() {

    private val mutableState = MutableStateFlow(LiveMapState())

    val state: StateFlow<LiveMapState> = mutableState.asStateFlow()

    init {
        // The plane is already connected — the shell does that. What it has no position for until
        // now is the passenger, and without one it joins no cells at all.
        viewModelScope.launch {
            locations.fixes.collect { fix ->
                live.onPosition(fix.asPoint())
                mutableState.update {
                    it.copy(fix = MapFix(fix.lat, fix.lng, fix.accuracyMetres))
                }
            }
        }
        viewModelScope.launch {
            live.vehicles.collect { frames -> mutableState.update { it.withFrames(frames) } }
        }
        viewModelScope.launch {
            live.status.collect { status -> mutableState.update { it.copy(status = status) } }
        }
        viewModelScope.launch { loadShortcuts() }
        viewModelScope.launch { loadRecents() }
    }

    /**
     * Re-reads §2.2's recents.
     *
     * Called when the map is resumed, because the row is written on **another screen**
     * (SCR-PA-008) and a local table has no change feed a `StateFlow` could subscribe to.
     */
    fun onResumed() {
        viewModelScope.launch { loadRecents() }
    }

    /** SCR-PA-006's mode row. Instant and client-side — no re-query (the wireframe says so). */
    fun setMode(mode: ServiceMode, enabled: Boolean) {
        mutableState.update { it.withFilter(it.filter.withMode(mode, enabled)) }
    }

    /** SCR-PA-006's type chips. */
    fun setType(type: lk.mageride.shared.data.models.VehicleType, enabled: Boolean) {
        mutableState.update { it.withFilter(it.filter.withType(type, enabled)) }
    }

    /**
     * A marker was tapped — MAP-07, routed by mode (AL-23).
     *
     * Returns what should happen rather than doing it: opening a sheet is this class's, navigating
     * to SCR-PA-024 is the screen's, and a `null` vehicle (a tap on a marker that has since left
     * the map) is neither.
     */
    fun onMarkerTapped(vehicleId: String): MarkerTap {
        val vehicle = mutableState.value.vehicles
            .firstOrNull { it.vehicleId == vehicleId }
            ?.let { drawn -> live.vehicles.value.firstOrNull { it.vehicleId == drawn.vehicleId } }
            ?: return MarkerTap.Ignored

        return when (vehicle.mode) {
            // Bus and train — route, distance, ETA, driver (US-7.4, US-7.11).
            ServiceMode.A -> {
                mutableState.update { it.copy(selected = vehicle, detail = null) }
                viewModelScope.launch { loadDetail(vehicle) }
                MarkerTap.ShowPopup(vehicle)
            }

            // AL-23 / US-4.6. The vehicle id is what SCR-PA-024 pre-fills.
            ServiceMode.B -> MarkerTap.RequestModeBAccess(vehicle.vehicleId)

            // US-7.4 — a standby vehicle shows nothing.
            ServiceMode.C, null -> MarkerTap.Ignored
        }
    }

    /** SCR-PA-007 dismissed. */
    fun dismissPopup() {
        mutableState.update { it.copy(selected = null, detail = null) }
    }

    /**
     * The popup's ETA, driver and plate — the fields the socket does not carry.
     *
     * **Centred on the passenger, not on the vehicle.** `NearbyVehicle.etaSeconds` is defined as
     * *"seconds to the querying passenger"*, so a lookup centred on the bus would answer roughly
     * zero and the popup would tell every passenger their bus was already here. Without a fix
     * there is no "to the passenger" at all, and the popup says the ETA is unavailable rather than
     * inventing a reference point.
     *
     * Best effort: this is three lines of detail on a sheet that already shows the vehicle and the
     * distance, so a failure leaves those standing rather than replacing the sheet with an error.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadDetail(vehicle: VehicleFrame) {
        val fix = mutableState.value.fix ?: return
        val metres = distanceMetres(GeoPoint(fix.lat, fix.lng), vehicle.point)
        try {
            val found = query.getNearbyVehicles(
                lat = fix.lat,
                lng = fix.lng,
                // The contract's band is 100–20 000 m, and the radius has to reach past the
                // vehicle or the snapshot comes back without the one thing it was asked for.
                radiusMetres = (metres.toInt() + RADIUS_MARGIN).coerceIn(MIN_RADIUS, MAX_RADIUS),
                modes = listOf(ServiceMode.A),
            ).vehicles.firstOrNull { it.vehicleId == vehicle.vehicleId } ?: return

            mutableState.update { state ->
                // The popup may have been dismissed, or another marker tapped, while this was in
                // flight — in which case this answer is about a vehicle nobody is looking at.
                if (state.selected?.vehicleId != vehicle.vehicleId) {
                    state
                } else {
                    state.copy(
                        detail = VehicleDetail(
                            etaSeconds = found.etaSeconds,
                            driverName = found.driverName,
                            registrationNumber = found.registrationNumber,
                        ),
                    )
                }
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // The sheet keeps the distance it computed itself.
        }
    }

    /**
     * The wireframe's *"Recent"* rows.
     *
     * Local-only (§2.2), so this is a read of the handset rather than of the platform — and it is
     * empty on a fresh install, which is what the wireframe's own sketch of one row means: a place
     * appears here after the passenger has chosen it on SCR-PA-008.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadRecents() {
        try {
            val rows = recents.recent()
            mutableState.update { it.copy(recents = rows) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // No rows. The search bar above them is the way to a destination regardless.
        }
    }

    /**
     * US-7.13's shortcuts.
     *
     * Best effort: the chips are a convenience and a passenger with no saved addresses simply has
     * none, which is also what a failure looks like. C083 owns the screen that creates them.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadShortcuts() {
        try {
            val saved = iam.listSavedAddresses().items
            mutableState.update { it.copy(shortcuts = saved) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // No chips. The search bar beside them still works.
        }
    }
}

/** The contract's radius band, and enough margin that the vehicle is inside it. */
private const val RADIUS_MARGIN = 200
private const val MIN_RADIUS = 100
private const val MAX_RADIUS = 20_000

/** Re-applies the filter to the frames the plane last published. */
private fun LiveMapState.withFrames(frames: List<VehicleFrame>): LiveMapState = copy(
    vehicles = frames.filter(filter::allows).map(VehicleFrame::toMapVehicle),
    // A vehicle that left the map cannot stay open in a popup over it.
    selected = selected?.takeIf { open -> frames.any { it.vehicleId == open.vehicleId } },
    detail = detail?.takeIf { selected != null && frames.any { it.vehicleId == selected.vehicleId } },
    lastFrames = frames,
)

/** Re-applies a changed filter to the frames already held, with no re-query. */
private fun LiveMapState.withFilter(next: MapFilter): LiveMapState = copy(
    filter = next,
    vehicles = lastFrames.filter(next::allows).map(VehicleFrame::toMapVehicle),
)
