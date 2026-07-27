package lk.mageride.shared.mqtt

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** The three payload shapes on the device plane (`mqtt-topics.md` §2). */
class MqttPayloadsTest {

    @Test
    fun a_position_sample_round_trips_through_cbor() {
        val original = sample(seq = 84_213)

        val decoded = PositionCodec.decode(PositionCodec.encode(original))

        assertEquals(original, decoded)
    }

    @Test
    fun the_cbor_payload_is_smaller_than_the_json_one() {
        // The reason the wire is CBOR at all: this runs on a metered mobile link at up to five
        // messages a second per vehicle.
        val original = sample(seq = 1)
        val cbor = PositionCodec.encode(original).size
        val json = lk.mageride.shared.serialization.MageRideJson
            .encodeToString(lk.mageride.shared.data.models.PositionSample.serializer(), original)
            .encodeToByteArray()
            .size

        assertTrue(cbor < json, "CBOR $cbor bytes vs JSON $json bytes")
    }

    @Test
    fun a_malformed_position_payload_is_reported_rather_than_thrown_at_the_socket() {
        assertNull(PositionCodec.decodeOrNull(byteArrayOf(0x01, 0x02, 0x03)))
    }

    @Test
    fun status_is_the_literal_online_or_offline() {
        assertEquals("offline", VehicleStatus.OFFLINE.wire)
        assertEquals("online", VehicleStatus.ONLINE.wire)
        assertEquals(VehicleStatus.OFFLINE, VehicleStatus.decode(VehicleStatus.OFFLINE.encode()))
        assertEquals(VehicleStatus.ONLINE, VehicleStatus.decode("online".encodeToByteArray()))
        assertNull(VehicleStatus.decode("gone".encodeToByteArray()))
    }

    @Test
    fun the_contract_form_of_a_cadence_hint_is_understood() {
        val payload = """{"cmd":"setPosRate","args":{"seconds":1},"expiresAt":"2026-07-27T09:20:00Z"}"""

        val delivery = MqttCommands.decode(payload, MQTT_EPOCH)

        val command = assertIs<CommandDelivery.Deliver>(delivery).command
        assertEquals(1.seconds, assertIs<MqttCommand.SetPosRate>(command).interval)
        assertEquals(MqttCommandName.SET_POS_RATE, command.envelope.name)
    }

    @Test
    fun the_add_form_of_a_cadence_hint_is_understood_too() {
        // ADD §7.5.1 and D5' §5.2 print `{"cmd":"setPosRate","intervalMs":2000}` — the interval at
        // the top level, in milliseconds. Ignoring it would mean silently publishing at the wrong
        // rate, which is exactly the failure R-07 exists to prevent. See the C017 handoff.
        val delivery = MqttCommands.decode("""{"cmd":"setPosRate","intervalMs":2000}""", MQTT_EPOCH)

        val command = assertIs<CommandDelivery.Deliver>(delivery).command
        assertEquals(2000.milliseconds, assertIs<MqttCommand.SetPosRate>(command).interval)
    }

    @Test
    fun an_expired_command_is_never_delivered() {
        // "expiresAt is honoured on reconnect — a reboot queued an hour ago is not something a
        // returning vehicle should obey."
        val payload = """{"cmd":"reboot","expiresAt":"2026-07-27T08:00:00Z"}"""

        val delivery = MqttCommands.decode(payload, MQTT_EPOCH)

        assertEquals(CommandRejection.EXPIRED, assertIs<CommandDelivery.Drop>(delivery).reason)
    }

    @Test
    fun a_command_with_no_expiry_never_lapses() {
        val delivery = MqttCommands.decode("""{"cmd":"pingNow"}""", MQTT_EPOCH)

        val command = assertIs<CommandDelivery.Deliver>(delivery).command
        assertEquals(MqttCommandName.PING_NOW, assertIs<MqttCommand.Other>(command).name)
    }

    @Test
    fun the_four_commands_this_module_does_not_act_on_stay_untyped() {
        // `mqtt-topics.md` §2.2 fixes the NAMES of all five commands but the `args` shape of only
        // setPosRate. Inventing field names for setGeofence here would put this module ahead of
        // the contract; C067/C085 and C043 handle these.
        listOf("pingNow", "reboot", "setGeofence", "revokeCredential").forEach { name ->
            val delivery = MqttCommands.decode("""{"cmd":"$name","args":{"x":1}}""", MQTT_EPOCH)

            val command = assertIs<CommandDelivery.Deliver>(delivery).command
            assertEquals(MqttCommandName.fromWire(name), assertIs<MqttCommand.Other>(command).name, name)
        }
    }

    @Test
    fun a_command_this_build_does_not_know_is_delivered_with_a_null_name() {
        val delivery = MqttCommands.decode("""{"cmd":"selfDestruct"}""", MQTT_EPOCH)

        val command = assertIs<CommandDelivery.Deliver>(delivery).command
        assertNull(assertIs<MqttCommand.Other>(command).name, "log it, do not guess")
        assertEquals("selfDestruct", command.envelope.cmd)
    }

    @Test
    fun a_malformed_command_is_dropped() {
        assertEquals(
            CommandRejection.MALFORMED,
            assertIs<CommandDelivery.Drop>(MqttCommands.decode("not json", MQTT_EPOCH)).reason,
        )
        assertEquals(
            CommandRejection.MALFORMED,
            assertIs<CommandDelivery.Drop>(MqttCommands.decode("""{"args":{}}""", MQTT_EPOCH)).reason,
        )
    }

    @Test
    fun a_cadence_hint_with_no_readable_interval_is_dropped() {
        val delivery = MqttCommands.decode("""{"cmd":"setPosRate","args":{"rate":"fast"}}""", MQTT_EPOCH)

        assertEquals(CommandRejection.MISSING_INTERVAL, assertIs<CommandDelivery.Drop>(delivery).reason)
    }
}
