package lk.mageride.driver.home

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import kotlinx.coroutines.CancellationException
import lk.mageride.shared.data.api.transit.TransitApi
import lk.mageride.shared.data.api.trip.TripStateApi
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.transit.TransitRoute
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.SessionState
import lk.mageride.shared.data.models.trip.StartSessionRequest

/**
 * What this handset remembers about a Mode A/B journey between launches.
 *
 * Two values, both of them about *this device* rather than about the platform, which is why
 * neither has an endpoint:
 *
 * * **[startedSessionId]** — the session the driver started **from the dashboard**. AL-32 needs the
 *   difference between a journey the driver began and one a paired GPS tracker began on ignition,
 *   and `trip-state.yaml`'s `Session` carries an `endReason` but **no start reason**: nothing on
 *   the wire says who opened it. Recording our own is the only thing that can tell them apart
 *   across a process death, and it is what raises the *"started automatically by GPS device"*
 *   banner for a session this app did not open.
 * * **[routeId]** — the bus route last run. `transit.yaml` has no "routes I drive" read (see the
 *   C070 handoff), so re-typing the number every morning is the alternative.
 */
internal interface JourneyPreferences {

    /** The session id this dashboard started, or `null` when it started none. */
    var startedSessionId: Ulid?

    /** The GTFS `route_id` last run on this handset. */
    var routeId: String?
}

/** [JourneyPreferences] over a private `SharedPreferences` file. Nothing here is a secret. */
internal class AndroidJourneyPreferences(context: Context) : JourneyPreferences {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override var startedSessionId: Ulid?
        get() = store.getString(KEY_SESSION, null)
        set(value) = store.edit { putString(KEY_SESSION, value) }

    override var routeId: String?
        get() = store.getString(KEY_ROUTE, null)
        set(value) = store.edit { putString(KEY_ROUTE, value) }

    private companion object {
        const val FILE = "driver_journey"
        const val KEY_SESSION = "started_session_id"
        const val KEY_ROUTE = "route_id"
    }
}

/**
 * SCR-DA-011's journey, as the dashboard sees it.
 *
 * @property session The live or just-ended tracking window; `null` for a parked vehicle.
 * @property route The Mode A bus route, resolved for the *"138 — Pettah ↔ Maharagama"* card.
 * @property startedByDevice Whether a paired GPS tracker opened this session on ignition (AL-32)
 *   rather than the driver opening it here.
 */
internal data class JourneyStanding(
    val session: Session? = null,
    val route: TransitRoute? = null,
    val startedByDevice: Boolean = false,
) {

    /** Whether the vehicle is live on its route — the state that offers **End Journey**. */
    val isRunning: Boolean get() = session?.state == SessionState.ACTIVE

    /**
     * Whether US-5.10's five-minute grace is open.
     *
     * Only an **automatic** end qualifies (the 30-minute idle timer, the 100 m destination
     * geofence, the broker's last will), which is exactly what `restartableUntil` being present
     * means — trip-state-svc sets it on `AUTO_ENDED` and on nothing else.
     */
    val isRestartable: Boolean
        get() = session?.state == SessionState.AUTO_ENDED && session.restartableUntil != null
}

/**
 * trip-state-svc — Mode A/B tracking sessions, and **nothing else** (R-01).
 *
 * **This is not ride-svc.** A session is a *vehicle running its route*: it has no fare, no offer
 * and no passenger of its own, and `ck_sessions_mode` refuses `mode: C` at the database. SCR-DA-011
 * talks here; SCR-DA-010, SCR-DA-014 and SCR-DA-015 talk to ride-svc and dispatch-svc. The
 * boundary is never crossed in either direction.
 *
 * **AL-32 — the dashboard outranks the device, in both directions.** A tracker that sees ignition
 * ON opens a session through `POST /v1/internal/sessions/ignition`, and this dashboard shows it as
 * already started; the driver may still End it here. A driver who starts a journey by hand gets a
 * session the tracker's ignition-OFF will not close, because trip-state-svc closes only *"one the
 * device started — never one the driver started from the dashboard"*.
 */
internal class JourneyRepository(
    private val tripState: TripStateApi,
    private val transit: TransitApi,
    private val preferences: JourneyPreferences,
) {

    /** `GET /v1/sessions/{vehicleId}/active` plus the route card, for one vehicle. */
    suspend fun standing(vehicleId: Ulid): JourneyStanding {
        val session = tripState.getActiveSession(vehicleId)
        return JourneyStanding(
            session = session,
            route = routeOf(session?.routeId ?: preferences.routeId),
            // A live session whose id this device never recorded is the tracker's (AL-32).
            startedByDevice = session != null && session.sessionId != preferences.startedSessionId,
        )
    }

    /**
     * `POST /v1/sessions/start` — the green **Start Journey**.
     *
     * The session id is recorded locally on the way out, because that record is the only thing
     * that can later say this journey was not the tracker's.
     *
     * `409 driver-already-live` when this driver already has a session running (D-03's mutex);
     * `403 vehicle-not-approved` for a vehicle the Fleet Portal has not approved.
     */
    suspend fun start(vehicleId: Ulid, mode: ServiceMode, routeId: String?, autoEndAtDestination: Boolean): Session {
        val session = tripState.startSession(
            StartSessionRequest(
                vehicleId = vehicleId,
                mode = mode,
                routeId = routeId,
                autoEndAtDestination = autoEndAtDestination,
            ),
        )
        preferences.startedSessionId = session.sessionId
        preferences.routeId = routeId
        return session
    }

    /** `POST /v1/sessions/{sessionId}/end` — the red **End Journey**, device session or not. */
    suspend fun end(sessionId: Ulid): Session {
        val session = tripState.endSession(sessionId)
        if (preferences.startedSessionId == sessionId) preferences.startedSessionId = null
        return session
    }

    /**
     * `POST /v1/sessions/{sessionId}/restart` — US-5.10's five-minute grace.
     *
     * `410 Gone` once the window has passed, which is a real answer rather than a failure: the
     * journey has to be started again from scratch.
     */
    suspend fun restart(sessionId: Ulid): Session {
        val session = tripState.restartSession(sessionId)
        preferences.startedSessionId = session.sessionId
        return session
    }

    /**
     * `GET /v1/transit/routes/{routeId}` — the route card's long name.
     *
     * Best-effort: an unknown route number, or a feed that has not been activated, leaves the card
     * showing the number the driver typed. Losing the long name must not stop a bus going live.
     */
    @Suppress("TooGenericExceptionCaught") // A missing route name never blocks Start Journey.
    suspend fun routeOf(routeId: String?): TransitRoute? {
        val id = routeId?.takeIf(String::isNotBlank) ?: return null
        return try {
            transit.getTransitRoute(id)
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            TransitRoute(routeId = id, routeShortName = id)
        }
    }
}
