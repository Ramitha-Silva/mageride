package lk.mageride.shared.mqtt

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The topic tree and its QoS/retain table (`mqtt-topics.md` §1, ADD §7.2, D6' §3.1). */
class MqttTopicsTest {

    @Test
    fun the_four_vehicle_topics_are_spelled_as_the_contract_prints_them() {
        assertEquals("veh/$TEST_VEHICLE/pos/live", MqttTopics.positionLive(TEST_VEHICLE))
        assertEquals("veh/$TEST_VEHICLE/pos/replay", MqttTopics.positionReplay(TEST_VEHICLE))
        assertEquals("veh/$TEST_VEHICLE/cmd", MqttTopics.command(TEST_VEHICLE))
        assertEquals("veh/$TEST_VEHICLE/status", MqttTopics.status(TEST_VEHICLE))
        assertEquals("sys/diag/$TEST_VEHICLE", MqttTopics.diagnostics(TEST_VEHICLE))
        assertEquals("fleet/OP1/+/pos/live", MqttTopics.fleetLive("OP1"))
    }

    @Test
    fun live_and_status_are_retained_and_replay_is_not() {
        // A retained backlog entry would be re-delivered to the next subscriber as though it were
        // the vehicle's current position.
        assertTrue(MqttTopicKind.POSITION_LIVE.retain)
        assertTrue(MqttTopicKind.STATUS.retain, "a late subscriber must still learn the vehicle is offline")
        assertFalse(MqttTopicKind.POSITION_REPLAY.retain)
        assertFalse(MqttTopicKind.COMMAND.retain)
    }

    @Test
    fun the_position_plane_is_qos_one_and_diagnostics_is_qos_zero() {
        listOf(
            MqttTopicKind.POSITION_LIVE,
            MqttTopicKind.POSITION_REPLAY,
            MqttTopicKind.COMMAND,
            MqttTopicKind.STATUS,
            MqttTopicKind.FLEET_LIVE,
        ).forEach { assertEquals(MqttQos.AT_LEAST_ONCE, it.qos, "$it") }

        assertEquals(MqttQos.AT_MOST_ONCE, MqttTopicKind.DIAGNOSTICS.qos)
        assertEquals(0, MqttQos.AT_MOST_ONCE.level)
        assertEquals(1, MqttQos.AT_LEAST_ONCE.level)
    }

    @Test
    fun only_cmd_travels_towards_the_device() {
        assertEquals(MqttDirection.BROKER_TO_DEVICE, MqttTopicKind.COMMAND.direction)
        assertEquals(MqttDirection.DEVICE_TO_BROKER, MqttTopicKind.POSITION_LIVE.direction)
        assertEquals(MqttDirection.DEVICE_TO_BROKER, MqttTopicKind.POSITION_REPLAY.direction)
    }

    @Test
    fun every_topic_a_driver_app_touches_round_trips_through_the_parser() {
        MqttTopics.forVehicle(TEST_VEHICLE).forEach { (kind, topic) ->
            val parsed = MqttTopics.parse(topic)

            assertEquals(kind, parsed?.kind, topic)
            assertEquals(TEST_VEHICLE, parsed?.vehicleId, topic)
        }
    }

    @Test
    fun a_fleet_wildcard_parses_to_its_operator() {
        val parsed = MqttTopics.parse("fleet/OP1/+/pos/live")

        assertEquals(MqttTopicKind.FLEET_LIVE, parsed?.kind)
        assertEquals("OP1", parsed?.operatorId)
        assertNull(parsed?.vehicleId, "the wildcard names no vehicle")
        assertEquals("V9", MqttTopics.parse("fleet/OP1/V9/pos/live")?.vehicleId)
    }

    @Test
    fun a_topic_from_outside_the_tree_is_not_ours() {
        assertNull(MqttTopics.parse("veh/$TEST_VEHICLE"))
        assertNull(MqttTopics.parse("veh/$TEST_VEHICLE/pos"))
        assertNull(MqttTopics.parse("veh/$TEST_VEHICLE/pos/history"))
        assertNull(MqttTopics.parse("telemetry/raw"))
        assertNull(MqttTopics.parse(""))
    }
}
