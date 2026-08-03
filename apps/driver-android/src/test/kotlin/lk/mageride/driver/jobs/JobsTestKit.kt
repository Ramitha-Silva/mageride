package lk.mageride.driver.jobs

import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.vehicle.FakeActiveVehicleStore
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Duration
import kotlin.time.ExperimentalTime

/** The board's first row in these tests. */
internal const val JOB_ONE: Ulid = "01JJOB00000000000000000001"

/** Its second. */
internal const val JOB_TWO: Ulid = "01JJOB00000000000000000002"

/**
 * A booking on the Job Board, [inFuture] from [Fixtures.NOW].
 *
 * The pickup time is expressed as an offset rather than as an instant because every rule this
 * cluster has is about the distance between now and the pickup — the T-30 go-live, the T-30
 * reminder, and whether a card is still worth bidding on.
 */
@OptIn(ExperimentalTime::class)
internal fun scheduledRide(
    id: Ulid = JOB_ONE,
    inFuture: Duration,
    now: Timestamp = Fixtures.NOW,
    status: ScheduledRideStatus = ScheduledRideStatus.SCHEDULED,
): ScheduledRide = ScheduledRide(
    scheduledRideId = id,
    pickup = Fixtures.PICKUP,
    dropoff = Fixtures.DROPOFF,
    vehicleType = RideVehicleType.SEDAN,
    pickupTime = now + inFuture,
    status = status,
    // The wireframe's "11.2 km". Every other field of a board row is fixed here because no rule in
    // this cluster reads one — what the Job Board and the upcoming list turn on is the clock.
    distanceM = 11_200,
    intentCount = 0,
)

/** A one-page answer for `listJobBoard` / `listDriverScheduledRides`. */
internal fun page(vararg rides: ScheduledRide): Page<ScheduledRide> = Page(items = rides.toList())

/**
 * [DriverIdentity] over a signed-in session.
 *
 * Only `driverId` is used by this cluster — nothing here reads a vehicle — but the identity is built
 * the way the app builds it rather than stubbed, because `driverId` being `null` is the state every
 * one of these view models silently does nothing in.
 */
internal fun identity(backend: FakeApiBackend, sessions: AuthSessionManager): DriverIdentity = DriverIdentity(
    registry = backend.mageRideApi().registry,
    sessions = sessions,
    activeVehicle = FakeActiveVehicleStore(),
)
