package lk.mageride.e2e

import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.domain.geo.GeoCells
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.platform.platformH3Grid
import lk.mageride.shared.realtime.LiveHub
import java.util.concurrent.ConcurrentHashMap

/** One vehicle out of a `VehiclePositions` batch, as Gson hands it over. */
internal class VehicleFrameDto {
    var vehicleId: String? = null
    var lat: Double = 0.0
    var lng: Double = 0.0
    var heading: Int? = null
    var speed: Double? = null
    var type: String? = null
    var mode: String? = null
}

/**
 * The passenger app's live map, minus the map: a real SignalR connection to `/hubs/live` that joins
 * the 19 res-7 cells of the 3 km view and collects the frames that arrive.
 *
 * The SignalR **Java** client is the one D6' §5 names for Android, and the method and event names
 * come from `:shared`'s [LiveHub] rather than string literals — SignalR resolves both by string,
 * so a typo is a handler that is never called rather than a compile error, and the whole reason
 * those names are spelled once in the shared module is to make that impossible.
 */
internal class PassengerLiveMap(private val environment: Environment, private val accessToken: String) {

    private val seen = ConcurrentHashMap<String, VehicleFrameDto>()
    private lateinit var connection: HubConnection

    /** Every vehicle this passenger has been told about, newest frame per vehicle. */
    val vehicles: Map<String, VehicleFrameDto> get() = seen.toMap()

    fun connect() {
        connection = HubConnectionBuilder
            .create(environment.gatewayUrl.trimEnd('/') + LiveHub.PATH)
            // The credential is the ordinary 30-minute API access token (D-29) — never the MQTT
            // session JWT, which is a different credential with a different audience (E-02). The
            // Java client puts it in the `access_token` query parameter, which is SignalR's own
            // convention and unavoidable: a browser WebSocket cannot set an Authorization header.
            .withAccessTokenProvider(Single.defer { Single.just(accessToken) })
            .build()

        connection.on(
            LiveHub.Event.VEHICLE_POSITIONS,
            { frames: Array<VehicleFrameDto> ->
                frames.forEach { frame -> frame.vehicleId?.let { seen[it] = frame } }
            },
            Array<VehicleFrameDto>::class.java,
        )

        connection.start().blockingAwait()
    }

    /**
     * Joins the 19 cells of the R-06 3 km view around [centre].
     *
     * The cells are computed by `:shared`'s [GeoCells] over the same `com.uber:h3` the app uses, so
     * they are the ids position-processor-svc writes its streams under. A client that computed them
     * any other way would join groups nothing publishes to — and see an empty map, not an error.
     */
    fun joinViewAround(centre: GeoPoint): Set<H3Cell> {
        val grid = platformH3Grid() ?: error("No H3 grid on this platform — the JVM actual should provide one.")
        val cells = GeoCells.viewCells(grid, centre)

        check(cells.size == GeoCells.PASSENGER_VIEW_CELL_COUNT) {
            "R-06 fixes the 3 km view at ${GeoCells.PASSENGER_VIEW_CELL_COUNT} cells; got ${cells.size}."
        }

        connection.send(LiveHub.Method.JOIN_GEOCELLS, cells.map { it.token }.toTypedArray())
        return cells
    }

    /** Waits until [vehicleId] has appeared in a batch, or gives up. */
    fun awaitVehicle(vehicleId: String, timeoutMs: Long): VehicleFrameDto? {
        val deadline = System.currentTimeMillis() + timeoutMs

        while (System.currentTimeMillis() < deadline) {
            seen[vehicleId]?.let { return it }
            Thread.sleep(POLL_MS)
        }

        return null
    }

    fun close() {
        if (::connection.isInitialized) {
            runCatching { connection.stop().blockingAwait() }
        }
    }

    private companion object {
        const val POLL_MS = 200L
    }
}
