package lk.mageride.driver.history

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.GeometrySource
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.query.TripPlane
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.trip.Rating
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-030 — the list, the detail that completes each row, and AL-35's rating sheet.
 *
 * **What makes the *"Rate ★"* link correct is the detail read, not the list read.** `TripSummary`
 * carries no rating, and query-svc's trip-detail SQL joins `trips.ratings` on `rater_id = @UserId`
 * — so for a driver, `TripDetail.rating` means *"the stars I already left"*. Without it a re-opened
 * screen would offer to rate a trip twice.
 */
@OptIn(ExperimentalTime::class)
class RideHistoryViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    private val rideId: Ulid = Fixtures.RIDE_ID
    private val sessionId: Ulid = Fixtures.TRIP_ID

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listTrips", Page(items = listOf(rideTrip())))
        backend.returns("getTrip", tripDetail())
        backend.returns("getRide", ride())
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_row_carries_the_distance_and_the_rating_the_summary_does_not() = runBlocking {
        val model = viewModel()
        val state = model.state.await { it.trips.firstOrNull()?.distanceKm != null }

        assertEquals(8.0, state.trips.single().distanceKm)
        assertNull(state.trips.single().rating)
        assertTrue(state.trips.single().isRateable)
    }

    @Test
    fun a_trip_this_driver_already_rated_offers_no_second_rating() = runBlocking {
        backend.returns("getTrip", tripDetail(rating = 5))

        val model = viewModel()
        val state = model.state.await { it.trips.firstOrNull()?.rating != null }

        assertEquals(5, state.trips.single().rating)
        assertFalse(state.trips.single().isRateable)

        model.openRating(rideId)
        assertNull(model.state.value.rating, "the sheet refuses to open on a rated trip")
    }

    @Test
    fun a_mode_a_or_b_session_is_never_rateable() = runBlocking {
        // A session is a vehicle's journey, not one person's trip: it has no single passenger, and
        // `DriverRatingInput` requires one. That is what a bus journey is, not a gap.
        backend.returns("listTrips", Page(items = listOf(sessionTrip())))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertFalse(state.trips.single().isRateable)
    }

    @Test
    fun one_dead_detail_does_not_take_the_list_down_with_it() = runBlocking {
        backend.fails("getTrip", io.ktor.http.HttpStatusCode.InternalServerError, "internal-error")

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(1, state.trips.size, "the row keeps its summary")
        assertNull(state.trips.single().distanceKm)
        assertNull(state.error, "a missing distance is not a screen-level failure")
    }

    @Test
    fun the_sheet_names_the_passenger_from_the_ride_because_a_trip_summary_has_none() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openRating(rideId)
        val sheet = model.state.await { it.rating?.loading == false }.rating

        assertEquals(Fixtures.PASSENGER_ID, sheet?.passengerId)
        assertEquals("Nimal", sheet?.passengerName)
        assertEquals(RatePassengerState.MAX_STARS, sheet?.stars, "the wireframe opens on five filled stars")
        assertTrue(sheet?.canSubmit == true)
    }

    @Test
    fun submitting_sends_the_stars_and_marks_the_row_rated() = runBlocking {
        backend.returns("rateSessionPassenger", rating(id = "01JRATING00000000000000001", stars = 4))

        val model = viewModel()
        model.state.await { !it.loading }
        model.openRating(rideId)
        model.state.await { it.rating?.loading == false }

        model.onStarsChange(4)
        model.onCommentChange("Polite and on time.")
        model.submitRating()

        val state = model.state.await { it.rating == null && it.trips.single().rating != null }
        assertEquals(4, state.trips.single().rating)
        assertFalse(state.trips.single().isRateable)

        val body = MageRideJson.parseToJsonElement(backend.lastCall("rateSessionPassenger").body).toString()
        assertTrue(body.contains("\"stars\":4"), body)
        assertTrue(body.contains(Fixtures.PASSENGER_ID), body)
        assertTrue(body.contains("Polite and on time."), body)
    }

    @Test
    fun the_only_rating_route_on_the_platform_is_the_session_one() = runBlocking {
        // C074's headline spec gap, asserted so it cannot be quietly "fixed" by pointing the write
        // somewhere that does not exist. `trips.ratings.subject_kind` accepts 'ride', query-svc
        // reads ride-subject ratings back, and `ride.yaml` declares no rating route at all — so the
        // subject id goes to trip-state-svc's session-scoped path, which is the one door there is.
        backend.returns("rateSessionPassenger", rating(id = "01JRATING00000000000000002", stars = 5))

        val model = viewModel()
        model.state.await { !it.loading }
        model.openRating(rideId)
        model.state.await { it.rating?.loading == false }
        model.submitRating()

        model.state.await { it.rating == null }
        assertEquals("/v1/sessions/$rideId/driver-rating", backend.lastCall("rateSessionPassenger").path)
    }

    @Test
    fun a_ride_booked_for_an_unregistered_rider_has_nobody_to_rate() = runBlocking {
        // P-01: a proxy booking's rider has no `iam.users` row, so there is no `ratee_id` and
        // `DriverRatingInput.passengerId` cannot be filled.
        backend.returns("getRide", ride(riderId = null, riderName = null))

        val model = viewModel()
        model.state.await { !it.loading }
        model.openRating(rideId)

        val sheet = model.state.await { it.rating?.loading == false }.rating
        assertNotNull(sheet)
        assertNull(sheet.passengerId)
        assertFalse(sheet.canSubmit)

        model.submitRating()
        assertFalse(backend.called("rateSessionPassenger"))
    }

    @Test
    fun the_star_count_is_clamped_to_the_contracts_own_range() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }
        model.openRating(rideId)
        model.state.await { it.rating?.loading == false }

        model.onStarsChange(0)
        assertEquals(RatePassengerState.MIN_STARS, model.state.value.rating?.stars)
        model.onStarsChange(9)
        assertEquals(RatePassengerState.MAX_STARS, model.state.value.rating?.stars)
    }

    private fun rating(id: Ulid, stars: Int): Rating = Rating(ratingId = id, stars = stars, createdAt = Fixtures.NOW)

    private fun rideTrip(): TripSummary = TripSummary(
        tripId = rideId,
        plane = TripPlane.RIDE,
        mode = ServiceMode.C,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        fareMinor = 48_000L,
        currency = Currency.LKR,
        startedAt = Fixtures.NOW,
        endedAt = Fixtures.NOW,
    )

    private fun sessionTrip(): TripSummary = TripSummary(
        tripId = sessionId,
        plane = TripPlane.SESSION,
        mode = ServiceMode.A,
        startedAt = Fixtures.NOW,
    )

    private fun tripDetail(rating: Int? = null): TripDetail = TripDetail(
        tripId = rideId,
        plane = TripPlane.RIDE,
        mode = ServiceMode.C,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        fareMinor = 48_000L,
        currency = Currency.LKR,
        startedAt = Fixtures.NOW,
        distanceKm = 8.0,
        rating = rating,
        geometrySource = GeometrySource.OPERATIONAL,
    )

    private fun ride(riderId: Ulid? = Fixtures.PASSENGER_ID, riderName: String? = "Nimal"): RideDetail = RideDetail(
        rideId = rideId,
        kind = RideKind.PASSENGER,
        state = RideState.CashSettled,
        version = 4,
        bookerId = Fixtures.PASSENGER_ID,
        riderId = riderId,
        riderName = riderName,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.THREE_WHEELER,
        paymentMethod = RidePaymentMethod.CASH,
        createdAt = Fixtures.NOW,
    )

    private suspend fun viewModel(): RideHistoryViewModel {
        val api = backend.mageRideApi()
        return main.own(
            RideHistoryViewModel(
                identity = identity(backend, signedInSessions(backend)),
                history = RideHistoryRepository(query = api.query, ride = api.ride, tripState = api.tripState),
            ),
        )
    }
}
