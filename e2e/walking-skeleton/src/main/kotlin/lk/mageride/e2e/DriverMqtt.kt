package lk.mageride.e2e

import com.hivemq.client.mqtt.MqttClient
import com.hivemq.client.mqtt.datatypes.MqttQos
import com.hivemq.client.mqtt.mqtt5.Mqtt5BlockingClient
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.mqtt.MqttTopics
import lk.mageride.shared.mqtt.PositionCodec
import java.util.Base64
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.time.Clock
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime

/**
 * The driver app's publishing half: a real MQTT 5 session against the real EMQX, presenting a real
 * session JWT to the real ACL.
 *
 * HiveMQ is the client D6' §3 names for Android, so this is the same library the Driver App will
 * use — and the payload goes through `:shared`'s own [PositionCodec], so what lands on
 * `veh/{vehicleId}/pos/live` is byte-for-byte what the app would publish.
 */
@OptIn(ExperimentalTime::class)
internal class DriverMqtt(
    private val environment: Environment,
    private val vehicleId: String,
    private val deviceId: String,
) {
    private lateinit var client: Mqtt5BlockingClient

    /** Monotonic per vehicle — the R-17/T-05 dedupe key. position-processor drops `seq <= seen`. */
    private var seq = System.currentTimeMillis() / 1000

    fun connect() {
        client = MqttClient.builder()
            .useMqttVersion5()
            .identifier("e2e-driver-${UUID.randomUUID()}")
            .serverHost(environment.mqttHost)
            .serverPort(environment.mqttPort)
            .buildBlocking()

        client.connectWith()
            .simpleAuth()
            // The username IS the principal. `emqx.conf`'s `verify_claims = { vehicleId =
            // "${username}" }` refuses the CONNECT unless the token agrees, and `acl.conf` writes
            // every device rule as `veh/${username}/*`.
            .username(vehicleId)
            .password(mintSessionToken().toByteArray())
            .applySimpleAuth()
            .cleanStart(true)
            .sessionExpiryInterval(0)
            .send()
    }

    /** Publishes one live sample at [point]. */
    fun publish(point: GeoPoint) {
        val sample = PositionSample(
            vehicleId = vehicleId,
            sampleTs = Clock.System.now(),
            seq = ++seq,
            lat = point.lat,
            lng = point.lng,
            speedMps = 8.0,
            headingDeg = 90,
            accuracyM = 5.0,
            source = PositionSource.MOBILE,
            mode = ServiceMode.C,
            vehicleType = VehicleType.THREE_WHEELER,
        )

        client.publishWith()
            .topic(MqttTopics.positionLive(vehicleId))
            .payload(PositionCodec.encode(sample))
            .qos(MqttQos.AT_LEAST_ONCE)
            .send()
    }

    fun disconnect() {
        if (::client.isInitialized) {
            runCatching { client.disconnect() }
        }
    }

    /**
     * Mints the MQTT session JWT (D6' §3.2, E-02).
     *
     * **This is the harness standing in for an endpoint that does not exist.** `iam.yaml` declares
     * `POST /v1/auth/mqtt-token` and C020 left it to C026; until then nothing can hand a device a
     * credential, so the run mints one against the same HMAC secret EMQX holds. A real client must
     * never do this — the whole point of E-02's decoupled token is that the platform issues it —
     * and this code should be deleted the day C026 lands.
     *
     * Hand-rolled rather than pulling in a JWT library: HS256 over two base64url segments is
     * thirty lines, and a dev credential whose shape is fixed by `emqx.conf` is better read than
     * configured.
     */
    private fun mintSessionToken(): String {
        val now = Clock.System.now()
        val expiry = now + SESSION_TTL

        val header = JsonObject(
            mapOf("alg" to JsonPrimitive("HS256"), "typ" to JsonPrimitive("JWT")),
        )

        val claims = JsonObject(
            mapOf(
                // Must equal the MQTT username, or the broker refuses the CONNECT.
                "vehicleId" to JsonPrimitive(vehicleId),
                "deviceId" to JsonPrimitive(deviceId),
                "sub" to JsonPrimitive(vehicleId),
                "jti" to JsonPrimitive(UUID.randomUUID().toString()),
                "iss" to JsonPrimitive("mageride-provisioning"),
                "iat" to JsonPrimitive(now.epochSeconds),
                "nbf" to JsonPrimitive(now.epochSeconds),
                "exp" to JsonPrimitive(expiry.epochSeconds),
            ),
        )

        val signingInput = "${encode(Json.encodeToString(JsonObject.serializer(), header))}." +
            encode(Json.encodeToString(JsonObject.serializer(), claims))

        val mac = Mac.getInstance("HmacSHA256").apply {
            init(SecretKeySpec(environment.mqttSecret.toByteArray(), "HmacSHA256"))
        }

        return "$signingInput.${encode(mac.doFinal(signingInput.toByteArray()))}"
    }

    private fun encode(value: String): String = encode(value.toByteArray())

    private fun encode(value: ByteArray): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(value)

    private companion object {
        /** D6' §3.2's floor: `max(active ride + 2 h, 4 h)`. A scripted ride never reaches it. */
        val SESSION_TTL = 4.hours
    }
}
