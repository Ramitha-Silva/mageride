package lk.mageride.shared.data.models

import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder

/**
 * The canonical position event — one GNSS fix from one vehicle.
 *
 * This is the single shape that travels the whole telemetry plane: the driver app and every
 * hardware tracker publish it to `veh/{vehicleId}/pos/live`, `position-processor-svc` (C039)
 * republishes it onto `telemetry.normalized`, and `persistence-writer-svc` (C040) COPYs it into
 * the `telemetry.positions` hypertable. CBOR on the wire, JSON-equivalent here
 * (`backend/contracts/realtime/mqtt-topics.md` §2.1, D6' §2.2).
 *
 * **Spec divergence — ADD Appendix A.** The C012 deliverable anchors this at ADD "Appendix A —
 * Position Event Schema (Canonical)", which prints an older, looser shape: `ts` as epoch millis,
 * `source` as a free string (`"mobile_gps | hardware_gt06 | hardware_st901"`), plus `altitude`,
 * `tripSessionId` and `traceId`, and **no `seq`**. Three later sources agree against it —
 * `mqtt-topics.md` §2.1 (the machine-checkable contract), D6' §2.2, and the landed
 * `telemetry.positions` DDL (C006), whose columns are `sample_ts`/`received_ts`/`seq`/`speed_mps`/
 * `heading_deg`/`accuracy_m`/`hdop`/`sat_count`/`source SMALLINT CHECK (source BETWEEN 0 AND 4)`.
 * Modelling Appendix A would produce a DTO that cannot be written to its own sink and that omits
 * `seq`, which is the replay dedupe key R-17/T-05 exists for. **The later, runnable shape wins**;
 * a micro-change-set against ADD Appendix A is recorded in the C012 handoff.
 *
 * `seq` is monotonic per vehicle and is layer 3 of the replay dedupe
 * (`ux_positions_vehicle_seq (vehicle_id, seq, sample_ts)`). A tracker that reconnects after
 * buffering bursts to `pos/replay` carrying the sequence numbers and GNSS timestamps it captured,
 * so an exact duplicate is rejected by the database and a near-duplicate by the Redis
 * `veh:seq:{vehicleId}` watermark.
 *
 * C017 owns the MQTT client, the adaptive publish cadence and the CBOR codec that carries this;
 * C012 owns only the shape.
 *
 * @property vehicleId The publishing vehicle. EMQX binds it from the device credential, so a
 *   device physically cannot publish under another vehicle's topic.
 * @property sampleTs GNSS capture instant — **not** the receive time. The hypertable's time
 *   dimension.
 * @property receivedTs When the platform saw it. `sampleTs` − `receivedTs` is the replay lag.
 * @property seq Monotonic per vehicle, `>= 0`. The replay dedupe key (R-17, T-05).
 * @property lat Degrees, −90…90. Range-checked by `ck_positions_lat`.
 * @property lng Degrees, −180…180. Range-checked by `ck_positions_lng`.
 * @property speedMps Ground speed in metres per second.
 * @property headingDeg Course over ground, 0…359.
 * @property accuracyM Horizontal accuracy in metres.
 * @property hdop Horizontal dilution of precision, as the receiver reports it.
 * @property satCount Satellites used in the fix.
 * @property source Which stack produced the fix.
 * @property mode The vehicle's operating mode at capture time.
 * @property vehicleType The vehicle's type, denormalised so a consumer needs no registry lookup.
 * @property fleetId Denormalised at write time for fleet-scoped reads and RLS; `null` when the
 *   vehicle is not fleet-owned. A vehicle that changes fleet keeps its old rows under the old
 *   fleet, which is what an audit trail should do (C006 decision 8).
 * @property tripId The Mode A/B tracking session this belongs to; absent for Mode C.
 */
@Serializable
public data class PositionSample(
    val vehicleId: Ulid,
    val sampleTs: Timestamp,
    val receivedTs: Timestamp? = null,
    val seq: Long,
    val lat: Double,
    val lng: Double,
    val speedMps: Double? = null,
    val headingDeg: Int? = null,
    val accuracyM: Double? = null,
    val hdop: Double? = null,
    val satCount: Int? = null,
    val source: PositionSource,
    val mode: ServiceMode? = null,
    val vehicleType: VehicleType? = null,
    val fleetId: Ulid? = null,
    val tripId: Ulid? = null,
) {
    /** The fix as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * Which stack produced a [PositionSample] (`telemetry.positions.source`, `ck_positions_source`).
 *
 * **Encoded as a small integer, not a name** — the column is a `SMALLINT` constrained to `0…4`
 * and the CBOR payload carries the same number, so the enum serialises through
 * [PositionSourceSerializer] rather than by `@SerialName`.
 *
 * @property code The wire value, `0…4`.
 */
@Serializable(with = PositionSourceSerializer::class)
public enum class PositionSource(public val code: Int) {
    /** The driver app's own GPS. */
    MOBILE(0),

    /** Concox GT06 / GT06N family, including TK103 and ST-901, via `adapter-gt06`. */
    GT06(1),

    /** JT/T 808 trackers via `adapter-jt808`. */
    JT808(2),

    /** H02 / H02X ASCII bus trackers via `adapter-h02`. */
    H02(3),

    /** Teltonika / Queclink firmware speaking NMEA over native MQTT — no adapter in the path. */
    NMEA_MQTT(4),
    ;

    /** Whether the fix came from a hardware tracker rather than the driver's handset (T-11). */
    public val isHardware: Boolean get() = this != MOBILE

    public companion object {
        private val BY_CODE: Map<Int, PositionSource> = entries.associateBy { it.code }

        /** Resolves a wire code, or `null` when it is outside the `0…4` CHECK domain. */
        public fun fromCode(code: Int): PositionSource? = BY_CODE[code]
    }
}

/** Encodes [PositionSource] as the `0…4` integer the topic contract and the DDL both use. */
public object PositionSourceSerializer : KSerializer<PositionSource> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("lk.mageride.PositionSource", PrimitiveKind.INT)

    override fun serialize(encoder: Encoder, value: PositionSource) {
        encoder.encodeInt(value.code)
    }

    override fun deserialize(decoder: Decoder): PositionSource {
        val code = decoder.decodeInt()
        return PositionSource.fromCode(code)
            ?: throw SerializationException("Unknown PositionSource code: $code")
    }
}
