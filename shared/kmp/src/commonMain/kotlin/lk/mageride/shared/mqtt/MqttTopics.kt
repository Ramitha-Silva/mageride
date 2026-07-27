package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.Ulid

// The EMQX topic tree, exactly as `backend/contracts/realtime/mqtt-topics.md` §1 prints it
// (ADD §7.2, D6' §3.1).
//
// THE TOPIC IS NOT WHERE AUTHORISATION LIVES. EMQX binds `{vehicleId}` from the device's JWT or
// X.509 claim, so a device physically cannot publish under another vehicle's topic. Building a
// topic for a vehicle the credential does not cover produces a rejected publish, not a leak —
// which is why these builders take the id and never a free-form string.

/** MQTT delivery guarantee. The position plane is QoS 1 throughout; only diagnostics is QoS 0. */
public enum class MqttQos(public val level: Int) {
    /** Fire and forget. */
    AT_MOST_ONCE(0),

    /** At least once — the broker redelivers until it is acknowledged. */
    AT_LEAST_ONCE(1),

    /** Exactly once. Not used by MageRide: the `seq` watermark already makes replay idempotent. */
    EXACTLY_ONCE(2),
}

/** Who publishes on a topic, and who consumes it. */
public enum class MqttDirection {
    /** The driver app or a tracker publishes; the broker fans it in. */
    DEVICE_TO_BROKER,

    /** The platform publishes; the device subscribes. */
    BROKER_TO_DEVICE,

    /** The platform publishes; a backend consumer or an operator subscribes. */
    BROKER_TO_CONSUMER,
}

/**
 * A branch of the topic tree with the QoS and retain flag the contract fixes for it.
 *
 * Retain is not a detail. `status` is retained so a consumer that subscribes after a device went
 * offline still learns it is offline; `pos/live` retains the last sample so a fresh subscriber
 * gets a position immediately instead of waiting a cadence tick. `pos/replay` must **not** be
 * retained — a retained backlog entry would be re-delivered as though it were current.
 *
 * @property qos Delivery guarantee for this branch.
 * @property retain Whether the broker keeps the last message.
 * @property direction Who publishes.
 */
public enum class MqttTopicKind(
    public val qos: MqttQos,
    public val retain: Boolean,
    public val direction: MqttDirection,
) {
    /** `veh/{vehicleId}/pos/live` — the hot path. */
    POSITION_LIVE(MqttQos.AT_LEAST_ONCE, retain = true, direction = MqttDirection.DEVICE_TO_BROKER),

    /** `veh/{vehicleId}/pos/replay` — offline backlog, rate-limited and never retained (R-17). */
    POSITION_REPLAY(MqttQos.AT_LEAST_ONCE, retain = false, direction = MqttDirection.DEVICE_TO_BROKER),

    /** `veh/{vehicleId}/cmd` — downlink; the device subscribes to its own only. */
    COMMAND(MqttQos.AT_LEAST_ONCE, retain = false, direction = MqttDirection.BROKER_TO_DEVICE),

    /** `veh/{vehicleId}/status` — `online` / `offline`, the LWT topic (R-15, T-04). */
    STATUS(MqttQos.AT_LEAST_ONCE, retain = true, direction = MqttDirection.BROKER_TO_CONSUMER),

    /** `fleet/{operatorId}/+/pos/live` — operator-scoped wildcard subscription. */
    FLEET_LIVE(MqttQos.AT_LEAST_ONCE, retain = false, direction = MqttDirection.BROKER_TO_CONSUMER),

    /** `sys/diag/{vehicleId}` — device diagnostics, QoS 0. */
    DIAGNOSTICS(MqttQos.AT_MOST_ONCE, retain = false, direction = MqttDirection.DEVICE_TO_BROKER),
}

/** A parsed topic: which branch, and whose. */
public data class MqttTopicRef(
    public val kind: MqttTopicKind,
    public val vehicleId: Ulid? = null,
    public val operatorId: Ulid? = null,
)

/** Builders and a parser for the topic tree. */
public object MqttTopics {

    private const val VEHICLE_ROOT = "veh"
    private const val FLEET_ROOT = "fleet"
    private const val DIAG_ROOT = "sys"

    /** Live GPS samples, `veh/{vehicleId}/pos/live`. */
    public fun positionLive(vehicleId: Ulid): String = "$VEHICLE_ROOT/$vehicleId/pos/live"

    /** Offline-buffered backlog, `veh/{vehicleId}/pos/replay`. */
    public fun positionReplay(vehicleId: Ulid): String = "$VEHICLE_ROOT/$vehicleId/pos/replay"

    /** Downlink commands, `veh/{vehicleId}/cmd` — the cadence hint arrives here (R-07). */
    public fun command(vehicleId: Ulid): String = "$VEHICLE_ROOT/$vehicleId/cmd"

    /** Presence, `veh/{vehicleId}/status` — the last-will topic. */
    public fun status(vehicleId: Ulid): String = "$VEHICLE_ROOT/$vehicleId/status"

    /** Device diagnostics, `sys/diag/{vehicleId}`. */
    public fun diagnostics(vehicleId: Ulid): String = "$DIAG_ROOT/diag/$vehicleId"

    /** An operator's whole fleet, `fleet/{operatorId}/+/pos/live`. Consumers only. */
    public fun fleetLive(operatorId: Ulid): String = "$FLEET_ROOT/$operatorId/+/pos/live"

    /**
     * The four topics a driver app touches for one vehicle.
     *
     * `cmd` is the only one it subscribes to; the other three it publishes (or wills) on.
     */
    public fun forVehicle(vehicleId: Ulid): Map<MqttTopicKind, String> = mapOf(
        MqttTopicKind.POSITION_LIVE to positionLive(vehicleId),
        MqttTopicKind.POSITION_REPLAY to positionReplay(vehicleId),
        MqttTopicKind.COMMAND to command(vehicleId),
        MqttTopicKind.STATUS to status(vehicleId),
        MqttTopicKind.DIAGNOSTICS to diagnostics(vehicleId),
    )

    /**
     * Reads a concrete topic back into [MqttTopicRef], or `null` if it is not one of ours.
     *
     * Used on the receive side: a client subscribed to more than one topic has to know which
     * branch a delivery came from before it can pick a codec.
     */
    @Suppress("ReturnCount")
    public fun parse(topic: String): MqttTopicRef? {
        val parts = topic.split('/')
        if (parts.size < MIN_SEGMENTS) return null
        return when (parts[0]) {
            VEHICLE_ROOT -> parseVehicle(parts)

            FLEET_ROOT -> parseFleet(parts)

            DIAG_ROOT -> if (parts.size == DIAG_SEGMENTS && parts[1] == "diag") {
                MqttTopicRef(MqttTopicKind.DIAGNOSTICS, vehicleId = parts[2])
            } else {
                null
            }

            else -> null
        }
    }

    /** `veh/{id}/cmd` is the shortest topic in the tree. */
    private const val MIN_SEGMENTS = 3

    /** `sys/diag/{vehicleId}`. */
    private const val DIAG_SEGMENTS = 3

    /** `fleet/{operatorId}/{vehicleId}/pos/live`. */
    private const val FLEET_SEGMENTS = 5

    /** How much of a fleet topic comes before `pos/live`. */
    private const val FLEET_PREFIX_SEGMENTS = 3

    private fun parseVehicle(parts: List<String>): MqttTopicRef? {
        val vehicleId = parts[1]
        val tail = parts.drop(2)
        val kind = when (tail) {
            listOf("pos", "live") -> MqttTopicKind.POSITION_LIVE
            listOf("pos", "replay") -> MqttTopicKind.POSITION_REPLAY
            listOf("cmd") -> MqttTopicKind.COMMAND
            listOf("status") -> MqttTopicKind.STATUS
            else -> return null
        }
        return MqttTopicRef(kind, vehicleId = vehicleId)
    }

    private fun parseFleet(parts: List<String>): MqttTopicRef? {
        if (parts.size != FLEET_SEGMENTS || parts.drop(FLEET_PREFIX_SEGMENTS) != listOf("pos", "live")) return null
        return MqttTopicRef(
            MqttTopicKind.FLEET_LIVE,
            vehicleId = parts[2].takeUnless { it == "+" },
            operatorId = parts[1],
        )
    }
}
