package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import kotlin.time.Instant

internal const val TEST_VEHICLE: Ulid = "01J8Z0000000000000VEHICLE"

internal val MQTT_EPOCH: Timestamp = Instant.parse("2026-07-27T09:00:00Z")

internal val COLOMBO: GeoPoint = GeoPoint(lat = 6.9271, lng = 79.8612)

/** A minimal `pos/live` sample — the fields `telemetry.positions` makes NOT NULL, and no more. */
internal fun sample(
    seq: Long,
    point: GeoPoint = COLOMBO,
    at: Timestamp = MQTT_EPOCH,
    vehicleId: Ulid = TEST_VEHICLE,
): PositionSample = PositionSample(
    vehicleId = vehicleId,
    sampleTs = at,
    seq = seq,
    lat = point.lat,
    lng = point.lng,
    source = PositionSource.MOBILE,
    mode = ServiceMode.C,
)
