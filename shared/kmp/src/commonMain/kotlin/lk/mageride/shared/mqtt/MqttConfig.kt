package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.MqttSessionToken
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * The broker's last will for one vehicle (R-15, T-04).
 *
 * EMQX publishes it when the device's socket dies without a clean disconnect, and three services
 * act on it: `trip-state-svc` auto-ends the Mode A/B session, `dispatch-svc` releases the active
 * offer, starts the R-15 grace window and clears the Directional filter (DT-04), and
 * `fleet-health-svc` updates its rollup. **Retained**, so a consumer that subscribes afterwards
 * still learns the vehicle is offline.
 *
 * @property topic `veh/{vehicleId}/status`.
 * @property payload Always [VehicleStatus.OFFLINE] — a will announces absence, nothing else.
 */
public data class MqttLastWill(public val topic: String, public val payload: VehicleStatus = VehicleStatus.OFFLINE) {
    /** QoS 1, as the topic contract fixes for `status`. */
    public val qos: MqttQos get() = MqttTopicKind.STATUS.qos

    /** Retained, as the topic contract fixes for `status`. */
    public val retain: Boolean get() = MqttTopicKind.STATUS.retain
}

/**
 * What the client presents at CONNECT.
 *
 * The credential is the **MQTT session JWT** (E-02) — never the API access token. It has its own
 * TTL (`max(active ride + 2 h, 4 h)`), its own audience and its own renewal loop in
 * [lk.mageride.shared.domain.auth.MqttSessionTokenManager], precisely so a failed API refresh in
 * poor coverage cannot take position publishing down mid-ride.
 *
 * **EMQX validates the JWT at CONNECT only**, so a rotation takes effect on the *next* connection:
 * a client watching `MqttSessionTokenManager.token` must reconnect when it changes, and must not
 * assume an in-flight session is re-authorised.
 *
 * @property clientId Per-device, stable across reconnects so the broker can resume the session.
 * @property username The vehicle the credential is bound to. EMQX authorises from the token's
 *   claim, not from this field.
 * @property password The MQTT session JWT.
 */
public data class MqttCredentials(
    public val clientId: String,
    public val username: String,
    public val password: String,
)

/**
 * Everything the native MQTT client needs, computed from the specs rather than from the app.
 *
 * **This module does not own the socket.** HiveMQ (Android, C067) and CocoaMQTT (iOS, C085) do;
 * D6' §3 makes that split explicit. What is shared is the configuration those two clients must
 * agree on — topics, QoS, retain, the will, the session semantics and the ceilings — because a
 * disagreement between the two apps shows up as a platform-wide inconsistency nobody can
 * reproduce on one handset.
 *
 * @property host EMQX host.
 * @property port 8883 (TLS) by default — the same listener MQTT-native trackers use.
 * @property useTls Never turn this off outside a local compose stack.
 * @property keepAlive MQTT PINGREQ interval. ADD §7.1 chooses MQTT partly for its "~2-byte
 *   keepalives"; no spec pins the number, and 60 s is the broker-friendly default. Tunable.
 * @property connectTimeout How long a CONNECT may take before the backoff starts.
 * @property cleanStart `false` — a **persistent session** (ADD §7.1). The broker holds QoS-1
 *   messages for a device that drops mid-tunnel, which is the whole reason ingest is MQTT.
 * @property sessionExpiry How long the broker keeps that session after a disconnect.
 */
public data class MqttConfig(
    public val host: String,
    public val port: Int = TLS_PORT,
    public val useTls: Boolean = true,
    public val keepAlive: Duration = DEFAULT_KEEP_ALIVE,
    public val connectTimeout: Duration = DEFAULT_CONNECT_TIMEOUT,
    public val cleanStart: Boolean = false,
    public val sessionExpiry: Duration = DEFAULT_SESSION_EXPIRY,
) {

    /** The will to register for [vehicleId] at CONNECT. */
    public fun lastWill(vehicleId: Ulid): MqttLastWill = MqttLastWill(MqttTopics.status(vehicleId))

    /**
     * The credential to present.
     *
     * The client id is the token's own device id, so a reconnect resumes the persistent session
     * rather than forking a second one — two sessions under one credential is how a vehicle ends
     * up with two publishers and an interleaved position stream (US-3.6).
     *
     * @param token The live MQTT session token, from `MqttSessionTokenManager.token`.
     */
    public fun credentials(token: MqttSessionToken): MqttCredentials =
        MqttCredentials(clientId = token.deviceId, username = token.vehicleId, password = token.jwt)

    public companion object {
        /** The TLS listener (D6' §4.1). */
        public const val TLS_PORT: Int = 8883

        /** Plain-TCP listener — local development only. */
        public const val TCP_PORT: Int = 1883

        /** See [MqttConfig.keepAlive]. */
        public val DEFAULT_KEEP_ALIVE: Duration = 60.seconds

        /** See [MqttConfig.connectTimeout]. */
        public val DEFAULT_CONNECT_TIMEOUT: Duration = 30.seconds

        /** See [MqttConfig.sessionExpiry]. */
        public val DEFAULT_SESSION_EXPIRY: Duration = 3600.seconds
    }
}

/**
 * The ceilings the platform enforces on the device plane (D-17, R-09, `mqtt-topics.md` §4).
 *
 * **These are misbehaviour ceilings, not target rates.** The expected rate is whatever
 * [AdaptiveRateEngine] computes; a client that plans against these numbers is planning to be
 * throttled. They are here so the client can stay *under* them — a suppressed publish also emits
 * `mqtt.rate_violation` into `audit.events`, which is a fraud signal, not a retry hint.
 */
public object MqttRateLimits {

    /**
     * **5 messages/second per `vehicleId`** on `veh/+/pos/live`, enforced by the EMQX rule engine.
     *
     * Sized to accommodate the 1-second near-geofence cadence *plus retries* (AL-12); the earlier
     * 2/s figure pre-dated phase-aware cadence and would falsely throttle the burst.
     */
    public const val LIVE_MSG_PER_SECOND: Int = 5

    /** `position-processor-svc`'s second line: more than 10 msg/s over 10 s is dropped and flagged. */
    public const val PROCESSOR_MSG_PER_WINDOW: Int = 10

    /** The window that second line measures over. */
    public val PROCESSOR_WINDOW: Duration = 10.seconds

    /** Backlog replay is throttled far harder: 20 samples/s/device on `pos/replay`. */
    public const val REPLAY_MSG_PER_SECOND: Int = 20

    /** EMQX's per-listener connection rate limit, with a per-ASN guardrail beside it (R-09). */
    public const val CONNECTIONS_PER_SECOND_PER_LISTENER: Int = 500
}
