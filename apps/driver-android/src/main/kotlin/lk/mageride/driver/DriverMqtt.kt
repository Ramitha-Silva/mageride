package lk.mageride.driver

import com.hivemq.client.mqtt.MqttClient
import com.hivemq.client.mqtt.datatypes.MqttQos
import com.hivemq.client.mqtt.mqtt5.Mqtt5BlockingClient
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.mqtt.MqttTopics
import lk.mageride.shared.mqtt.PositionCodec
import java.util.UUID
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * The driver's position publisher — HiveMQ to EMQX, exactly as D6' §3 names it for Android.
 *
 * **Not a foreground service, and a real Driver App must be one.** D6' §3 puts the MQTT client "in
 * a native foreground service" so publishing survives the app going to background mid-ride; C076
 * owns that. A shell that published only while its Activity was resumed would look like it worked
 * and lose a ride's whole track the moment the screen locked.
 *
 * The payload is `:shared`'s [PositionCodec] over `:shared`'s [PositionSample], on the topic
 * `:shared`'s [MqttTopics] builds — so what lands on `veh/{vehicleId}/pos/live` is what
 * position-processor-svc expects, byte for byte.
 */
@OptIn(ExperimentalTime::class)
internal class DriverMqtt(
    private val host: String,
    private val port: Int,
    private val vehicleId: String,
) {
    private var client: Mqtt5BlockingClient? = null

    /** Monotonic per vehicle — the R-17/T-05 dedupe key; a rewind makes the vehicle go dark. */
    private var seq = System.currentTimeMillis() / 1000

    /**
     * Connects with [sessionJwt] as the MQTT password and the vehicle id as the username.
     *
     * `emqx.conf` refuses the CONNECT unless the token's `vehicleId` claim equals the username, and
     * `acl.conf` writes every device rule under `veh/{username}/` — so the username is the whole
     * identity, and a token minted for another vehicle authorises nothing.
     */
    fun connect(sessionJwt: String) {
        val mqtt = MqttClient.builder()
            .useMqttVersion5()
            .identifier("driver-${UUID.randomUUID()}")
            .serverHost(host)
            .serverPort(port)
            .buildBlocking()

        mqtt.connectWith()
            .simpleAuth()
            .username(vehicleId)
            .password(sessionJwt.toByteArray())
            .applySimpleAuth()
            .cleanStart(true)
            .send()

        client = mqtt
    }

    /** Publishes one live sample. QoS 1, as every topic in the tree is (D6' §3.1). */
    fun publish(point: GeoPoint) {
        val mqtt = client ?: return

        val sample = PositionSample(
            vehicleId = vehicleId,
            sampleTs = Clock.System.now(),
            seq = ++seq,
            lat = point.lat,
            lng = point.lng,
            source = PositionSource.MOBILE,
            mode = ServiceMode.C,
            vehicleType = VehicleType.THREE_WHEELER,
        )

        mqtt.publishWith()
            .topic(MqttTopics.positionLive(vehicleId))
            .payload(PositionCodec.encode(sample))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
    }

    fun disconnect() {
        runCatching { client?.disconnect() }
        client = null
    }
}
