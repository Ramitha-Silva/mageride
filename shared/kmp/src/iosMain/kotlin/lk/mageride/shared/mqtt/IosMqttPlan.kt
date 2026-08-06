package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.MqttSessionToken
import platform.Foundation.NSData

/**
 * Everything a CocoaMQTT client needs for one session, computed by `:shared`.
 *
 * **The Swift side connects a socket and nothing else.** The username is the vehicle id, the
 * credential is the MQTT session JWT (E-02, never the API access token), the last will is on
 * `veh/{vehicleId}/status` and the session is persistent — every one of those is a contract
 * `MqttConfig`, `MqttTopics` and `MqttTopicKind` already carry, and a Swift file that restated one
 * would be the second copy. This is the same rule
 * `apps/driver-android/.../location/MqttPositionTransport.kt` states for the HiveMQ half.
 *
 * **`emqx.conf` refuses the CONNECT unless the token's `vehicleId` claim equals the username**, and
 * `acl.conf` writes every device rule under `veh/{username}/`, so the username is the whole identity
 * and a token minted for another vehicle authorises nothing. [MqttConfig.credentials] builds the
 * triple; nothing assembles one by hand.
 *
 * **EMQX validates the JWT at CONNECT only**, so a rotation takes effect on the *next* connection.
 * That is why the app reconnects when `MqttSessionTokenManager.token` changes rather than pushing a
 * token into a live session.
 *
 * @property clientId CONNECT client identifier.
 * @property username The vehicle id.
 * @property password The MQTT session JWT.
 * @property host EMQX host.
 * @property port 1883 plain, 8883 TLS.
 * @property useTls Never false outside a local compose stack.
 * @property keepAliveSeconds The 60 s keepalive ADD §7.1 fixes.
 * @property connectTimeoutSeconds How long a CONNECT may take before it is a failure.
 * @property cleanStart `false` — a PERSISTENT session, so the broker holds QoS-1 messages for a
 *   device that drops mid-tunnel. That is the whole reason ingest is MQTT rather than HTTP.
 * @property sessionExpirySeconds How long the broker keeps that session after a disconnect.
 * @property willTopic `veh/{vehicleId}/status`.
 * @property willPayload `offline`, published by the broker if the device vanishes (R-15, T-04).
 * @property willQos QoS for the will and for every status publish.
 * @property willRetain Retained, so a late subscriber learns the state without waiting.
 * @property statusTopic The same topic, for the explicit `online` / `offline` publishes.
 * @property commandTopic `veh/{vehicleId}/cmd` — the only topic a device subscribes to.
 * @property commandQos QoS to subscribe with.
 */
public data class IosMqttPlan(
    val clientId: String,
    val username: String,
    val password: String,
    val host: String,
    val port: Int,
    val useTls: Boolean,
    val keepAliveSeconds: Int,
    val connectTimeoutSeconds: Int,
    val cleanStart: Boolean,
    val sessionExpirySeconds: Long,
    val willTopic: String,
    val willPayload: NSData,
    val willQos: Int,
    val willRetain: Boolean,
    val statusTopic: String,
    val commandTopic: String,
    val commandQos: Int,
) {

    /**
     * The `online` announcement, published right after a successful CONNECT.
     *
     * @return the retained `online` payload for [statusTopic].
     */
    public fun onlinePayload(): NSData = VehicleStatus.ONLINE.encode().toNSData()

    /**
     * The `offline` announcement, published **before** a deliberate DISCONNECT.
     *
     * The broker does *not* fire the last will on a graceful DISCONNECT, so without this publish a
     * driver who goes offline deliberately is indistinguishable from one whose phone died — except
     * that nobody is told at all (R-15, T-04).
     */
    public fun offlinePayload(): NSData = VehicleStatus.OFFLINE.encode().toNSData()

    public companion object {

        /** Builds the plan for [token]'s vehicle under [config]. */
        public fun of(config: MqttConfig, token: MqttSessionToken): IosMqttPlan {
            val credentials = config.credentials(token)
            val will = config.lastWill(token.vehicleId)
            return IosMqttPlan(
                clientId = credentials.clientId,
                username = credentials.username,
                password = credentials.password,
                host = config.host,
                port = config.port,
                useTls = config.useTls,
                keepAliveSeconds = config.keepAlive.inWholeSeconds.toInt(),
                connectTimeoutSeconds = config.connectTimeout.inWholeSeconds.toInt(),
                cleanStart = config.cleanStart,
                sessionExpirySeconds = config.sessionExpiry.inWholeSeconds,
                willTopic = will.topic,
                willPayload = will.payload.encode().toNSData(),
                willQos = will.qos.qosLevel,
                willRetain = will.retain,
                statusTopic = MqttTopics.status(token.vehicleId),
                commandTopic = MqttTopics.command(token.vehicleId),
                commandQos = MqttTopicKind.COMMAND.qos.qosLevel,
            )
        }

        /** The status topic for [vehicleId], for a caller that has no token in hand. */
        public fun statusTopic(vehicleId: Ulid): String = MqttTopics.status(vehicleId)
    }
}

/** `:shared`'s QoS as the integer every MQTT client on earth takes. */
internal val MqttQos.qosLevel: Int
    get() = when (this) {
        MqttQos.AT_MOST_ONCE -> 0
        MqttQos.AT_LEAST_ONCE -> 1
        MqttQos.EXACTLY_ONCE -> 2
    }
