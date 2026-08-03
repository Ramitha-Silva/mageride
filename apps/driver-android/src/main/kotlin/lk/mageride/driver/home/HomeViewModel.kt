package lk.mageride.driver.home

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.PositionPublisher
import lk.mageride.driver.location.asPoint
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.shared.data.models.dispatch.PresenceState
import lk.mageride.shared.domain.geo.distanceMetres
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-010 · the Mode C standby dashboard** and **SCR-DA-011 · the Mode A/B Start/End Journey
 * dashboard** — one destination, one state machine, two sheets.
 *
 * Four rules live here and nowhere else on this surface:
 *
 * * **US-9.6 — go-online is gated on a vehicle, not on a wish.** [HomeState.canGoOnline] is
 *   `LiveVehicle.canGoOnline`, which is `VehicleSummary.canGoLive` (C069's rule, once); with no
 *   eligible vehicle the toggle is dead and the empty state routes to SCR-DA-026a.
 * * **Going online is two things or it is neither.** `POST /v1/standby/online` puts the driver in
 *   the candidate pool and `PositionForegroundService` starts publishing on `veh/{id}/pos/live`. A
 *   driver in the pool who is not publishing is offered rides that cannot find them, so the
 *   publisher starts only after the call is accepted and stops before the offline call is made.
 * * **DT-04 — going offline clears the Directional filter**, and the use is not refunded. The
 *   server does it; this mirrors it locally rather than leaving a stale chip on the sheet.
 * * **AL-32 — the dashboard outranks the tracker.** Start and End are sent regardless of what the
 *   GPS device has done; the banner reports the device's doing, it does not gate ours.
 *
 * @param location The handset's own GNSS. The home map draws **only** this vehicle (AL-31), and
 *   `GoOnlineRequest` needs a position in its body — with no fix yet, going online is refused with
 *   copy rather than sent with a lie.
 */
@OptIn(ExperimentalTime::class)
internal class HomeViewModel(
    private val identity: DriverIdentity,
    private val standby: StandbyRepository,
    private val journeys: JourneyRepository,
    private val rides: ActiveRideRepository,
    private val location: DriverLocationSource,
    private val publisher: PositionPublisher,
) : ViewModel() {

    private val mutableState = MutableStateFlow(HomeState())

    val state: StateFlow<HomeState> = mutableState.asStateFlow()

    init {
        refresh()
        observeDevice()
    }

    /** Re-reads the vehicle, the standing, the journey and any ride already in hand. */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { state -> state.copy(loading = false) } }) {
            val driverId = identity.driverId
            val vehicles = identity.liveVehicle()
            val live = vehicles.live

            mutableState.update {
                it.copy(
                    loading = false,
                    vehicles = vehicles,
                    standing = if (driverId == null) it.standing else standby.standing(driverId),
                    // SCR-DA-011 only. A Mode C vehicle has no tracking session and asking
                    // trip-state-svc about one would be the R-01 fence crossed for nothing.
                    journey = if (live != null && vehicles.isScheduledMode) {
                        journeys.standing(live.vehicleId)
                    } else {
                        JourneyStanding()
                    },
                    activeRideId = driverId?.let { id -> rides.active(id)?.rideId },
                )
            }
        }
    }

    /**
     * The wireframe's big **◉ ONLINE — Mode C** toggle (US-6A.1).
     *
     * Refused outright with no vehicle: a toggle that moved and then sprang back would read as a
     * broken control rather than as the gate it is.
     */
    fun toggleOnline(desired: Boolean) {
        val current = mutableState.value
        val vehicleId = current.vehicles.live?.vehicleId
        val position = current.position?.asPoint()

        // Two refusals, both of them things the driver can act on: US-9.6's "no vehicle available"
        // and "the first fix has not arrived". Sending a `GoOnlineRequest` with a made-up point
        // would put the driver on the map somewhere they are not, and dispatch would score them
        // from it.
        val refusal = when {
            !current.canGoOnline || vehicleId == null -> R.string.home_needs_vehicle
            desired && position == null -> R.string.home_waiting_for_gps
            else -> null
        }
        if (refusal != null || vehicleId == null) {
            mutableState.update { it.copy(error = refusal) }
            return
        }

        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { state -> state.copy(busy = false) } }) {
            if (desired) {
                val presence = standby.goOnline(vehicleId, requireNotNull(position))
                publisher.start(vehicleId)
                mutableState.update { it.copy(busy = false, online = presence != PresenceState.OFFLINE) }
            } else {
                // Stop publishing first: a driver who is still on `veh/{id}/pos/live` after
                // dispatch has taken them out of the pool is a vehicle on the map that cannot be
                // hired, which US-7.16 is the passenger-side half of.
                publisher.stop()
                standby.goOffline()
                mutableState.update {
                    it.copy(
                        busy = false,
                        online = false,
                        // DT-04. The activation is spent either way (US-6A.19) — `usesRemaining`
                        // is deliberately carried through unchanged.
                        standing = it.standing.copy(
                            directional = it.standing.directional?.copy(
                                active = false,
                                destination = null,
                                expiresAt = null,
                                timeRemainingSec = 0,
                            ),
                        ),
                    )
                }
            }
        }
    }

    /** SCR-DA-011's green **Start Journey**. Sent whatever the tracker has done (AL-32). */
    fun startJourney() {
        val current = mutableState.value
        val vehicle = current.vehicles.live ?: return
        mutableState.update { it.copy(busy = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { state -> state.copy(busy = false) } }) {
            val session = journeys.start(
                vehicleId = vehicle.vehicleId,
                mode = vehicle.mode,
                routeId = current.journey.route?.routeId,
                autoEndAtDestination = current.autoEndAtDestination,
            )
            publisher.start(vehicle.vehicleId)
            mutableState.update {
                it.copy(
                    busy = false,
                    journeyDistanceM = 0.0,
                    journey = it.journey.copy(session = session, startedByDevice = false),
                )
            }
        }
    }

    /**
     * SCR-DA-011's red **End Journey**, and US-5.10's **Restart** inside the five-minute grace.
     *
     * One entry point because they are the same button in two states, and because which one is
     * legal is a property of the session rather than of the tap.
     */
    fun endOrRestartJourney() {
        val current = mutableState.value
        val session = current.journey.session ?: return
        val vehicleId = current.vehicles.live?.vehicleId ?: return
        val restarting = current.journey.isRestartable
        mutableState.update { it.copy(busy = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { state -> state.copy(busy = false) } }) {
            val updated = if (restarting) {
                journeys.restart(session.sessionId).also { publisher.start(vehicleId) }
            } else {
                journeys.end(session.sessionId).also { publisher.stop() }
            }
            mutableState.update {
                it.copy(
                    busy = false,
                    journeyDistanceM = if (restarting) it.journeyDistanceM else 0.0,
                    journey = it.journey.copy(session = updated, startedByDevice = false),
                )
            }
        }
    }

    /** The route card's **Change ▾**. Resolved against the active GTFS feed for its long name. */
    fun chooseRoute(routeId: String) {
        launchGuarded {
            val route = journeys.routeOf(routeId)
            mutableState.update { it.copy(journey = it.journey.copy(route = route)) }
        }
    }

    /** US-5.4's 100 m destination geofence, armed for the **next** journey. */
    fun setAutoEndAtDestination(enabled: Boolean) {
        mutableState.update { it.copy(autoEndAtDestination = enabled) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * The two device-side tickers: the driver's own position, and the clock the timers run on.
     *
     * The distance is accumulated from consecutive fixes with `:shared`'s haversine — the same
     * formula R-06's post-filter and DT-02's detour test use, so *"18.4 km"* on SCR-DA-011 means
     * what the platform means by a kilometre. It is a **device** measurement and says so: the
     * authoritative distance for a Mode A journey is what `position-processor-svc` reconstructs
     * from the published track.
     */
    private fun observeDevice() {
        viewModelScope.launch {
            location.fixes.collect { fix -> mutableState.update { it.advancedBy(fix) } }
        }
        viewModelScope.launch {
            while (isActive) {
                mutableState.update { it.copy(tickAt = Clock.System.now()) }
                delay(TICK)
            }
        }
    }

    /**
     * Runs [block] on the view-model scope, turning any failure into trilingual copy (D-26).
     *
     * @param onFailure Applied before the message is set, so a control that was spinning stops.
     */
    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the user raw.
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

    private companion object {

        /** One second — fine enough for a `01:12:40` timer and for a Directional countdown. */
        val TICK = 1.seconds
    }
}

/**
 * Folds one fix into the state: the marker moves, and a running journey grows by the leg.
 *
 * Only while the session is actually running — a bus parked between journeys must not accumulate
 * the drive home into the last one's distance.
 */
private fun HomeState.advancedBy(fix: Fix): HomeState {
    val previous = position
    val leg = if (previous != null && journey.isRunning) {
        distanceMetres(previous.asPoint(), fix.asPoint())
    } else {
        0.0
    }
    return copy(position = fix, journeyDistanceM = journeyDistanceM + leg)
}
