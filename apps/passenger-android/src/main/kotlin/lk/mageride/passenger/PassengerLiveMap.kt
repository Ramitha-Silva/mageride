package lk.mageride.passenger

import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.domain.geo.GeoCells
import lk.mageride.shared.platform.platformH3Grid
import lk.mageride.shared.realtime.LiveHub

/** One vehicle out of a `VehiclePositions` batch, as the SignalR client's Gson hands it over. */
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
 * The live map's data half — a real SignalR connection to `/hubs/live`.
 *
 * There is deliberately no map here. C077 owns MapLibre over PMTiles; this delivers the frames and
 * the shell renders them as a list, which is enough to prove the socket, the geocell maths and the
 * fan-out actually connect.
 *
 * Method and event names come from `:shared`'s [LiveHub], never from string literals: SignalR
 * resolves both by string, so a typo is a handler that is never called rather than a compile error.
 */
internal class PassengerLiveMap(
    private val baseUrl: String,
    private val accessToken: String,
    private val onFrames: (List<VehicleMarker>) -> Unit,
) {
    private var connection: HubConnection? = null
    private val seen = LinkedHashMap<String, VehicleMarker>()

    /**
     * Connects and joins the 3 km view around [centre].
     *
     * @return the cell ids joined — 19 of them (R-06).
     */
    fun connectAround(centre: GeoPoint): List<String> {
        val hub = HubConnectionBuilder
            .create(baseUrl.trimEnd('/') + LiveHub.PATH)
            // The 30-minute API access token (D-29) in the `access_token` query parameter — never
            // the MQTT session JWT, which is a different credential entirely (E-02).
            .withAccessTokenProvider(Single.defer { Single.just(accessToken) })
            .build()

        hub.on(
            LiveHub.Event.VEHICLE_POSITIONS,
            { frames: Array<VehicleFrameDto> ->
                frames.forEach { frame ->
                    val id = frame.vehicleId ?: return@forEach
                    seen[id] = VehicleMarker(id, frame.lat, frame.lng, frame.type)
                }
                onFrames(seen.values.toList())
            },
            Array<VehicleFrameDto>::class.java,
        )

        hub.start().blockingAwait()
        connection = hub

        val grid = platformH3Grid() ?: error("No H3 grid bound on this platform.")
        val cells = GeoCells.viewCells(grid, centre).map { it.token }

        hub.send(LiveHub.Method.JOIN_GEOCELLS, cells.toTypedArray())
        return cells
    }

    fun close() {
        runCatching { connection?.stop()?.blockingAwait() }
        connection = null
    }
}
