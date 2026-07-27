package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideKind
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

// Builders for the C015 ride tests. Everything here is a value; the state machine has no I/O, so
// there is no harness to build — the one piece of C015 that talks to the network is OfferSession,
// and it uses C013's own MockEngine kit.

/** The instant the ride tests measure from, so an `expiresAt` in an assertion is readable. */
@OptIn(ExperimentalTime::class)
internal val RIDE_EPOCH: Instant = Instant.parse("2026-07-27T09:00:00Z")

internal const val TEST_RIDE_ID: Ulid = "01JRIDETESTTESTTESTTESTTES"

/** A snapshot at [state], with an offer deadline only where one makes sense. */
@OptIn(ExperimentalTime::class)
internal fun rideSnapshot(
    state: RideState = RideState.Requested,
    kind: RideKind = RideKind.PASSENGER,
    version: RideVersion = 1,
    offerExpiresAt: Timestamp? = null,
): RideSnapshot = RideSnapshot(
    rideId = TEST_RIDE_ID,
    kind = kind,
    state = state,
    version = version,
    offerExpiresAt = offerExpiresAt,
)

/** A projection on a clock the test drives by hand. */
@OptIn(ExperimentalTime::class)
internal fun projectionAt(
    state: RideState = RideState.Requested,
    kind: RideKind = RideKind.PASSENGER,
    version: RideVersion = 1,
    offerExpiresAt: Timestamp? = null,
    now: () -> Timestamp = { RIDE_EPOCH },
): RideProjection = RideProjection(
    initial = rideSnapshot(state = state, kind = kind, version = version, offerExpiresAt = offerExpiresAt),
    clock = now,
)

/**
 * A package projection sharing [handoff] with the caller.
 *
 * Separate from [projectionAt] because the test needs a reference to the gates it is about to
 * drive, and `RideProjection` would otherwise build its own.
 */
@OptIn(ExperimentalTime::class)
internal fun packageProjectionAt(state: RideState, handoff: PackageHandoff): RideProjection = RideProjection(
    initial = rideSnapshot(state = state, kind = RideKind.PACKAGE),
    handoff = handoff,
    clock = { RIDE_EPOCH },
)
