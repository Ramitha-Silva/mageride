package lk.mageride.shared.realtime

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.serialization.MageRideJson
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * The `/hubs/live` contract (`backend/contracts/realtime/signalr-hub.md`).
 *
 * SignalR resolves methods and events by **string**, so nothing here is a compile error at a call
 * site — a typo shows up as a handler that is simply never invoked. These assertions are the
 * spelling check the compiler cannot do.
 */
class LiveHubTest {

    @Test
    fun the_connection_settings_are_the_ones_the_contract_prints() {
        assertEquals("/hubs/live", LiveHub.PATH)
        assertEquals("access_token", LiveHub.ACCESS_TOKEN_QUERY_PARAM)
        assertEquals(15.seconds, LiveHub.KEEP_ALIVE)
        assertEquals(30.seconds, LiveHub.SERVER_TIMEOUT)
    }

    @Test
    fun the_url_carries_the_token_in_the_query_because_a_websocket_cannot_set_a_header() {
        assertEquals(
            "https://api.mageride.lk/hubs/live?access_token=abc.def.ghi",
            LiveHub.url("https://api.mageride.lk/", "abc.def.ghi"),
        )
        assertEquals(
            "https://api.mageride.lk/hubs/live?access_token=abc.def.ghi",
            LiveHub.url("https://api.mageride.lk", "abc.def.ghi"),
        )
    }

    @Test
    fun the_four_client_methods_and_seven_server_events_are_spelled_correctly() {
        assertEquals("JoinGeocells", LiveHub.Method.JOIN_GEOCELLS)
        assertEquals("LeaveGeocells", LiveHub.Method.LEAVE_GEOCELLS)
        assertEquals("SubscribeRide", LiveHub.Method.SUBSCRIBE_RIDE)
        assertEquals("SubscribeLocRequest", LiveHub.Method.SUBSCRIBE_LOC_REQUEST)

        assertEquals("VehiclePositions", LiveHub.Event.VEHICLE_POSITIONS)
        assertEquals("VehicleRemoved", LiveHub.Event.VEHICLE_REMOVED)
        assertEquals("RideStateChanged", LiveHub.Event.RIDE_STATE_CHANGED)
        assertEquals("DriverPosition", LiveHub.Event.DRIVER_POSITION)
        assertEquals("LocationRequestResolved", LiveHub.Event.LOCATION_REQUEST_RESOLVED)
        assertEquals("ShareRevoked", LiveHub.Event.SHARE_REVOKED)
        assertEquals("PackageStatus", LiveHub.Event.PACKAGE_STATUS)
    }

    @Test
    fun the_three_group_names_follow_the_contract() {
        val cell = H3Cell.parse("87611cb11ffffff")

        assertEquals("cell:87611cb11ffffff", LiveHub.cellGroup(cell))
        assertEquals("ride:R1", LiveHub.rideGroup("R1"))
        assertEquals("booker:B1:loc-req:Q1", LiveHub.bookerLocationRequestGroup("B1", "Q1"))
    }

    @Test
    fun a_vehicle_frame_round_trips_as_camel_case_json() {
        val frame = VehicleFrame(vehicleId = "V1", lat = 6.9271, lng = 79.8612, heading = 270, speed = 11.8)

        val json = MageRideJson.encodeToString(VehicleFrame.serializer(), frame)

        assertTrue("\"vehicleId\":\"V1\"" in json, json)
        assertEquals(frame, MageRideJson.decodeFromString(VehicleFrame.serializer(), json))
        assertEquals(GeoPoint(6.9271, 79.8612), frame.point)
    }

    @Test
    fun the_removal_reasons_are_lowercase_on_the_wire() {
        VehicleRemovalReason.entries.forEach {
            assertEquals(it.wire, it.name.lowercase())
            assertEquals(
                "\"${it.wire}\"",
                MageRideJson.encodeToString(VehicleRemovalReason.serializer(), it),
            )
        }
        assertEquals(setOf("stale", "offline", "engaged"), VehicleRemovalReason.entries.map { it.wire }.toSet())
    }

    @Test
    fun the_socket_payloads_reuse_the_rest_contracts_own_types() {
        // "Payload field names and value sets match the REST contracts exactly" — a client that
        // had two spellings of a ride state would render one of them wrong.
        val change = MageRideJson.decodeFromString(
            RideStateChanged.serializer(),
            """{"rideId":"R1","state":"InProgress","version":4,"etaSeconds":180}""",
        )

        assertEquals(RideState.InProgress, change.state)
        assertEquals(4, change.version)

        val resolved = MageRideJson.decodeFromString(
            LocationRequestResolved.serializer(),
            """{"requestId":"Q1","state":"Confirmed","geo":{"lat":6.9,"lng":79.8}}""",
        )
        assertEquals(LocationRequestState.Confirmed, resolved.state)

        val parcel = MageRideJson.decodeFromString(
            PackageStatusChanged.serializer(),
            """{"rideId":"R1","status":"Delivered"}""",
        )
        assertEquals(PackageStatus.Delivered, parcel.status)
    }

    @Test
    fun a_removal_and_a_revocation_name_the_vehicle_to_drop() {
        val removed = MageRideJson.decodeFromString(
            VehicleRemoved.serializer(),
            """{"vehicleId":"V1","reason":"engaged"}""",
        )

        assertEquals(VehicleRemovalReason.ENGAGED, removed.reason)
        assertEquals("V1", MageRideJson.decodeFromString(ShareRevoked.serializer(), """{"vehicleId":"V1"}""").vehicleId)
    }
}
