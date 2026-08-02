package lk.mageride.driver.location

import com.hivemq.client.mqtt.MqttClient
import com.hivemq.client.mqtt.mqtt5.Mqtt5BlockingClient
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.MqttSessionToken
import lk.mageride.shared.mqtt.MqttConfig
import lk.mageride.shared.mqtt.MqttTopicKind
import lk.mageride.shared.mqtt.MqttTopics
import lk.mageride.shared.mqtt.PositionCodec
import lk.mageride.shared.mqtt.VehicleStatus
import java.util.concurrent.atomic.AtomicBoolean
import com.hivemq.client.mqtt.datatypes.MqttQos as HiveQos

/**
 * The HiveMQ socket — D6' §3: *"Driver App client = HiveMQ (Android) … in a native foreground
 * service"*.
 *
 * `:shared` owns everything about *what* goes on the wire: the topics ([MqttTopics]), the QoS and
 * retain flags ([MqttTopicKind]), the CBOR payload ([PositionCodec]), the last will and the
 * credential ([MqttConfig]). This class owns only the socket, and deliberately reaches for those
 * rather than restating any of them — a topic string spelled here would be the second copy of a
 * contract that already has one.
 *
 * **The username is the vehicle id.** `emqx.conf` refuses the CONNECT unless the token's
 * `vehicleId` claim equals it and `acl.conf` writes every device rule under `veh/{username}/`, so
 * the username is the whole identity and a token minted for another vehicle authorises nothing.
 * [MqttConfig.credentials] builds the triple; this never assembles one by hand.
 *
 * **EMQX validates the JWT at CONNECT only.** A rotation therefore takes effect on the *next*
 * connection, which is why [connect] is called again whenever `MqttSessionTokenManager.token`
 * changes rather than the token being pushed into a live session.
 */
internal class MqttPositionTransport(private val config: MqttConfig) : PositionTransport {

    private val connected = AtomicBoolean(false)
    private var client: Mqtt5BlockingClient? = null
    private var vehicle: Ulid? = null

    override val isConnected: Boolean get() = connected.get() && client?.state?.isConnected == true

    /**
     * Opens a session for [token]'s vehicle, replacing any that is already open.
     *
     * @return whether the broker accepted the CONNECT.
     */
    @Suppress("TooGenericExceptionCaught") // The boundary: HiveMQ throws unchecked from several layers.
    fun connect(token: MqttSessionToken): Boolean {
        disconnect()

        val credentials = config.credentials(token)
        val will = config.lastWill(token.vehicleId)

        return try {
            val mqtt = MqttClient.builder()
                .useMqttVersion5()
                .identifier(credentials.clientId)
                .serverHost(config.host)
                .serverPort(config.port)
                .apply { if (config.useTls) sslWithDefaultConfig() }
                .buildBlocking()

            mqtt.connectWith()
                .simpleAuth()
                .username(credentials.username)
                .password(credentials.password.toByteArray())
                .applySimpleAuth()
                // A PERSISTENT session (ADD §7.1): the broker holds QoS-1 messages for a device
                // that drops mid-tunnel, which is the whole reason ingest is MQTT rather than HTTP.
                .cleanStart(config.cleanStart)
                .sessionExpiryInterval(config.sessionExpiry.inWholeSeconds)
                .keepAlive(config.keepAlive.inWholeSeconds.toInt())
                .willPublish()
                .topic(will.topic)
                .payload(will.payload.encode())
                .qos(will.qos.hive())
                .retain(will.retain)
                .applyWillPublish()
                .send()

            client = mqtt
            vehicle = token.vehicleId
            connected.set(true)
            announce(VehicleStatus.ONLINE)
            true
        } catch (_: Exception) {
            // No network, a rejected credential, a broker that is down. The caller retries with
            // `ReconnectBackoff`; a throw here would take the foreground service with it.
            connected.set(false)
            false
        }
    }

    override fun publishLive(sample: PositionSample): Boolean =
        publish(MqttTopics.positionLive(sample.vehicleId), PositionCodec.encode(sample), MqttTopicKind.POSITION_LIVE)

    override fun publishReplay(sample: PositionSample): Boolean = publish(
        MqttTopics.positionReplay(sample.vehicleId),
        PositionCodec.encode(sample),
        MqttTopicKind.POSITION_REPLAY,
    )

    /**
     * Subscribes to `veh/{vehicleId}/cmd` — the only topic a device subscribes to.
     *
     * `setPosRate` arrives here (R-07); [handler] gets the raw bytes and `MqttCommands.decode`
     * turns them into a typed command.
     */
    @Suppress("TooGenericExceptionCaught")
    fun subscribeCommands(handler: (ByteArray) -> Unit) {
        val mqtt = client ?: return
        val id = vehicle ?: return
        try {
            mqtt.toAsync()
                .subscribeWith()
                .topicFilter(MqttTopics.command(id))
                .qos(MqttTopicKind.COMMAND.qos.hive())
                .callback { handler(it.payloadAsBytes) }
                .send()
        } catch (_: Exception) {
            // A failed subscription costs cadence hints, not the position stream. The next
            // reconnect re-subscribes.
        }
    }

    /**
     * Closes the session cleanly, announcing `offline` first.
     *
     * A clean DISCONNECT means the broker does **not** publish the last will, so the explicit
     * `status` publish is what tells trip-state-svc, dispatch-svc and fleet-health-svc that the
     * vehicle went away on purpose (R-15, T-04). Without it a driver who goes offline deliberately
     * looks identical to one whose phone died — except that nobody is told at all.
     */
    @Suppress("TooGenericExceptionCaught")
    fun disconnect() {
        val mqtt = client ?: return
        try {
            announce(VehicleStatus.OFFLINE)
            mqtt.disconnect()
        } catch (_: Exception) {
            // Already gone. Nothing to clean up that the broker will not clean up itself.
        } finally {
            connected.set(false)
            client = null
            vehicle = null
        }
    }

    private fun announce(status: VehicleStatus) {
        val id = vehicle ?: return
        publish(MqttTopics.status(id), status.encode(), MqttTopicKind.STATUS)
    }

    @Suppress("TooGenericExceptionCaught")
    private fun publish(topic: String, payload: ByteArray, kind: MqttTopicKind): Boolean {
        val mqtt = client ?: return false
        return try {
            mqtt.publishWith()
                .topic(topic)
                .payload(payload)
                .qos(kind.qos.hive())
                .retain(kind.retain)
                .send()
            true
        } catch (_: Exception) {
            connected.set(false)
            false
        }
    }
}

/** `:shared`'s QoS as HiveMQ's. Two enums for one concept, so the mapping lives in exactly one place. */
private fun lk.mageride.shared.mqtt.MqttQos.hive(): HiveQos = when (this) {
    lk.mageride.shared.mqtt.MqttQos.AT_MOST_ONCE -> HiveQos.AT_MOST_ONCE
    lk.mageride.shared.mqtt.MqttQos.AT_LEAST_ONCE -> HiveQos.AT_LEAST_ONCE
    lk.mageride.shared.mqtt.MqttQos.EXACTLY_ONCE -> HiveQos.EXACTLY_ONCE
}
