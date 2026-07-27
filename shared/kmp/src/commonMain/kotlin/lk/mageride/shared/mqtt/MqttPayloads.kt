package lk.mageride.shared.mqtt

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerializationException
import kotlinx.serialization.cbor.Cbor
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.serialization.MageRideJson
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

// The three payload shapes on the device plane (`mqtt-topics.md` §2, D6' §2.2/§3.1):
// CBOR PositionSample on pos/live + pos/replay, a JSON command envelope on cmd, and the literal
// strings `online` / `offline` on status.
//
// CBOR IS NOT A DIFFERENT MODEL. It is the same `PositionSample` C012 owns, encoded compactly for
// a metered mobile link; the JSON in D6' §2.2 is that shape written out for humans. Anything that
// changes the DTO changes both wires at once, which is exactly what a shared module is for.

/** Presence on `veh/{vehicleId}/status` — the retained LWT payload (R-15, T-04). */
public enum class VehicleStatus(public val wire: String) {
    /** Published by the device after a successful CONNECT. */
    ONLINE("online"),

    /** The broker's last will, or the TCP adapter's emulation of it on socket half-close (T-04). */
    OFFLINE("offline"),
    ;

    /** The literal bytes on the wire. */
    public fun encode(): ByteArray = wire.encodeToByteArray()

    public companion object {
        private val BY_WIRE = entries.associateBy { it.wire }

        /** Reads a status payload, or `null` when it is neither literal. */
        public fun decode(bytes: ByteArray): VehicleStatus? = BY_WIRE[bytes.decodeToString().trim()]
    }
}

/**
 * CBOR codec for `pos/live` and `pos/replay`.
 *
 * `ignoreUnknownKeys` mirrors [MageRideJson]: the platform is versioned but additive, and a field
 * a newer `position-processor-svc` starts emitting must not crash an older build. Everything else
 * stays strict — a malformed number in a position sample is a bug to surface, not to coerce.
 */
@OptIn(ExperimentalSerializationApi::class)
public object PositionCodec {

    /** The one CBOR instance the device plane uses. */
    public val cbor: Cbor = Cbor { ignoreUnknownKeys = true }

    /** Encodes a sample for publication. */
    public fun encode(sample: PositionSample): ByteArray = cbor.encodeToByteArray(PositionSample.serializer(), sample)

    /** Decodes a received sample. */
    public fun decode(bytes: ByteArray): PositionSample = cbor.decodeFromByteArray(PositionSample.serializer(), bytes)

    /** Decodes a received sample, answering `null` rather than throwing on a malformed payload. */
    public fun decodeOrNull(bytes: ByteArray): PositionSample? = try {
        decode(bytes)
    } catch (_: SerializationException) {
        null
    } catch (_: IllegalArgumentException) {
        null
    }
}

/** The five downlink commands EMQX will deliver on `veh/{vehicleId}/cmd` (D6' §3.1). */
public enum class MqttCommandName(public val wire: String) {
    /** Change the position publish cadence (R-07). The only one this module acts on. */
    SET_POS_RATE("setPosRate"),

    /** Publish one sample now, regardless of cadence. */
    PING_NOW("pingNow"),

    /** Hardware only — restart the tracker. */
    REBOOT("reboot"),

    /** Hardware only — set an on-device geofence. */
    SET_GEOFENCE("setGeofence"),

    /** Credential revoked (T-12): the device must disconnect and re-provision. */
    REVOKE_CREDENTIAL("revokeCredential"),
    ;

    public companion object {
        private val BY_WIRE = entries.associateBy { it.wire }

        /** Resolves a wire name, or `null` for one outside the supported set. */
        public fun fromWire(wire: String): MqttCommandName? = BY_WIRE[wire]
    }
}

/**
 * A downlink command as it arrives: `{cmd, args, expiresAt}`.
 *
 * @property cmd The raw command name — kept as text so an unsupported one can still be logged.
 * @property args Command arguments; no spec fixes their shape for anything but `setPosRate`.
 * @property expiresAt When the command stops being valid. **Honoured on reconnect** — an expired
 *   command is not delivered to a device that comes back later, because a `reboot` queued an hour
 *   ago is not something a returning vehicle should obey.
 */
public data class MqttCommandEnvelope(
    public val cmd: String,
    public val args: JsonObject? = null,
    public val expiresAt: Timestamp? = null,
) {
    /** The typed name, or `null` if the platform sent something this build does not know. */
    public val name: MqttCommandName? get() = MqttCommandName.fromWire(cmd)

    /** Whether the command has lapsed at [now]. A command with no `expiresAt` never lapses. */
    public fun isExpired(now: Timestamp): Boolean = expiresAt != null && now >= expiresAt
}

/** A command that survived decoding and expiry. */
public sealed interface MqttCommand {

    /** The envelope it came from, so a handler can reach `expiresAt` and any unmodelled args. */
    public val envelope: MqttCommandEnvelope

    /**
     * `setPosRate` — the server-pushed cadence hint (R-07, ADD §7.5.1).
     *
     * @property interval How often to publish from now on. Feed it to
     *   [AdaptiveRateEngine.onCadenceHint], which clamps it against the broker ceiling.
     */
    public data class SetPosRate(public val interval: Duration, override val envelope: MqttCommandEnvelope) :
        MqttCommand

    /**
     * Any other command in the supported set.
     *
     * Deliberately untyped: `mqtt-topics.md` §2.2 fixes the *names* of all five commands but the
     * `args` shape of only `setPosRate`, and inventing field names for `setGeofence` here would
     * put this module ahead of the contract. The app shell (C067/C085) and the tracker adapters
     * (C043) handle these; a shared type would have to be revised the moment the contract grew
     * one.
     *
     * @property name `null` when the platform sent a command this build does not know — log it,
     *   do not guess.
     */
    public data class Other(public val name: MqttCommandName?, override val envelope: MqttCommandEnvelope) :
        MqttCommand
}

/** Why a downlink command was not acted on. */
public enum class CommandRejection {
    /** `expiresAt` had passed by the time it was delivered. */
    EXPIRED,

    /** Not JSON, or no `cmd` field. */
    MALFORMED,

    /** `setPosRate` carried no interval this build could read. */
    MISSING_INTERVAL,
}

/** The outcome of reading one `cmd` message. */
public sealed interface CommandDelivery {

    /** Act on it. */
    public data class Deliver(public val command: MqttCommand) : CommandDelivery

    /** Drop it, for [reason]. */
    public data class Drop(public val reason: CommandRejection, public val cmd: String? = null) : CommandDelivery
}

/**
 * Reads `veh/{vehicleId}/cmd` payloads.
 *
 * **A tolerant reader for `setPosRate`, on purpose.** Two specs print the hint two ways:
 * ADD §7.5.1 and D5' §5.2 write `{"cmd":"setPosRate","intervalMs":2000}` — the interval at the top
 * level, in milliseconds — while `mqtt-topics.md` §2.2 and D6' §3.1 write the general envelope
 * `{"cmd":"setPosRate","args":{"seconds":1},"expiresAt":…}`. The envelope is the machine-checkable
 * contract and is what this decodes into, but a client that understood only one spelling would
 * silently ignore a real cadence hint and keep publishing at the wrong rate — the failure R-07
 * exists to prevent. So all four readings are accepted: `args.intervalMs`, `args.seconds`,
 * `args.intervalSec`, and a top-level `intervalMs`. **A micro-change-set against ADD §7.5.1 /
 * D5' §5.2 is recorded in the C017 handoff** — the two documents should print one shape and one
 * unit.
 */
public object MqttCommands {

    private const val FIELD_CMD = "cmd"
    private const val FIELD_ARGS = "args"
    private const val FIELD_EXPIRES_AT = "expiresAt"
    private const val FIELD_INTERVAL_MS = "intervalMs"
    private const val FIELD_SECONDS = "seconds"
    private const val FIELD_INTERVAL_SEC = "intervalSec"

    /** Decodes a payload received at [now]. */
    public fun decode(bytes: ByteArray, now: Timestamp): CommandDelivery = decode(bytes.decodeToString(), now)

    /**
     * Decodes a payload received at [now].
     *
     * One `return` per way a command can fail to be actionable — malformed, unnamed, expired, or
     * carrying no readable interval — and each names the rejection it produces.
     */
    @Suppress("ReturnCount")
    public fun decode(payload: String, now: Timestamp): CommandDelivery {
        val root = parseObject(payload) ?: return CommandDelivery.Drop(CommandRejection.MALFORMED)
        val cmd = root[FIELD_CMD]?.asStringOrNull() ?: return CommandDelivery.Drop(CommandRejection.MALFORMED)

        val envelope = MqttCommandEnvelope(
            cmd = cmd,
            args = root[FIELD_ARGS] as? JsonObject,
            expiresAt = root[FIELD_EXPIRES_AT]?.asStringOrNull()?.let(::parseTimestampOrNull),
        )

        if (envelope.isExpired(now)) return CommandDelivery.Drop(CommandRejection.EXPIRED, cmd)

        if (envelope.name != MqttCommandName.SET_POS_RATE) {
            return CommandDelivery.Deliver(MqttCommand.Other(envelope.name, envelope))
        }

        val interval = readInterval(envelope.args, root)
            ?: return CommandDelivery.Drop(CommandRejection.MISSING_INTERVAL, cmd)
        return CommandDelivery.Deliver(MqttCommand.SetPosRate(interval, envelope))
    }

    @Suppress("ReturnCount")
    private fun readInterval(args: JsonObject?, root: JsonObject): Duration? {
        if (args != null) {
            args[FIELD_INTERVAL_MS]?.asLongOrNull()?.let { return it.milliseconds }
            args[FIELD_SECONDS]?.asLongOrNull()?.let { return it.seconds }
            args[FIELD_INTERVAL_SEC]?.asLongOrNull()?.let { return it.seconds }
        }
        return root[FIELD_INTERVAL_MS]?.asLongOrNull()?.milliseconds
    }

    private fun parseObject(payload: String): JsonObject? = try {
        MageRideJson.parseToJsonElement(payload) as? JsonObject
    } catch (_: SerializationException) {
        null
    } catch (_: IllegalArgumentException) {
        null
    }
}

private fun JsonElement.asStringOrNull(): String? = (this as? JsonPrimitive)?.takeIf { it.isString }?.content

private fun JsonElement.asLongOrNull(): Long? =
    (this as? JsonPrimitive)?.takeUnless { it.isString }?.content?.toDoubleOrNull()?.toLong()

private fun parseTimestampOrNull(raw: String): Timestamp? = try {
    Timestamp.parse(raw)
} catch (_: IllegalArgumentException) {
    null
}
