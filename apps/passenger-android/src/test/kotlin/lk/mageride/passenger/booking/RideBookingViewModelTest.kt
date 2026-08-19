package lk.mageride.passenger.booking

import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.location.LastKnownFix
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.onboarding.FakeAppPreferences
import lk.mageride.passenger.settings.PaymentPreference
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.transit.TransitCoverage
import lk.mageride.shared.data.models.transit.TransitLeg
import lk.mageride.shared.data.models.transit.TransitOption
import lk.mageride.shared.data.models.transit.TransitOptionKind
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitStop
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-009, and the two fences that are the reason it is a view model.
 *
 * **AL-19 is asserted as a type**, not as a rendering: [TierQuote] has no ETA and no distance
 * field, so the first test below reads as trivial and is not — it is the compile-time guarantee
 * written down, so that a future change that adds `etaSeconds` to the quote fails here rather than
 * quietly appearing on a card before a driver has been matched.
 *
 * **AL-18/AL-55** is the other half: transit-svc failing or having no feed must not stop a
 * passenger booking a tuk, and that is the difference between an error state and a muted row.
 */
class RideBookingViewModelTest {

    private val main = MainDispatcher()
    private val lastFix = LastKnownFix()
    private val bookings = FakeBookingRepository()
    private val draft = BookingDraft(PaymentPreference(FakeAppPreferences()), lastFix)
    private val keys = IdempotencyKeyGenerator { CLIENT_REQUEST_ID }

    @BeforeTest
    fun setUp() {
        main.install()
        draft.begin(dropoff = NUGEGODA, pickup = COLOMBO)
    }

    @Test
    fun a_pickup_captured_on_another_screen_re_quotes_the_journey() = runBlocking {
        // `refresh()`'s own KDoc says *"called on entry and whenever an end of the journey moves"*,
        // and nothing made the second half true. SCR-PA-010b captures a proxy pickup into the
        // shared draft and pops back here; this view model lives on the same back-stack entry and
        // survives the trip, so the fares and the bus routes on screen were still the ones quoted
        // from where the BOOKER was standing. Naming the rider's pin in the summary without this
        // would make the screen contradict itself — the right pickup over the wrong price.
        val model = viewModel()
        model.state.await { it.tiers.isNotEmpty() }
        assertEquals(COLOMBO.point, bookings.askedFaresFrom.first(), "the opening quote is the booker's fix")

        draft.expect(CaptureTarget.PROXY_PICKUP)
        draft.capture(MAHARAGAMA_PIN)

        model.state.await { it.draft.pickup == MAHARAGAMA_PIN }
        awaitCall("the fares are re-asked from the pin") { bookings.askedFaresFrom.contains(MAHARAGAMA_PIN.point) }
        assertTrue(bookings.askedRoutesFrom.contains(MAHARAGAMA_PIN.point), "and the bus list with it")
    }

    @Test
    fun a_pickup_somebody_picked_is_not_the_passengers_own_fix() = runBlocking {
        // **The defect this test exists for.** A `Place` cannot say where it came from, and the two
        // sources render differently on SCR-PA-009: the passenger's own fix carries no address and
        // is honestly "Current location", while a proxy rider's pin carries no address EITHER and
        // is a specific place somewhere else entirely. Without this flag the summary told a booker
        // their ride started where THEY were standing — the one thing booking for someone else
        // means it does not — and the coordinates the booker had just pinned were nowhere on
        // screen. Reported from a handset.
        lastFix.record(PassengerFix(lat = COLOMBO.lat, lng = COLOMBO.lng))
        draft.begin(dropoff = NUGEGODA)
        assertFalse(draft.current.pickupIsChosen, "the fix nobody chose is not a choice")

        // SCR-PA-010b's Map method, which is a pin and nothing else — no address, on purpose.
        draft.expect(CaptureTarget.PROXY_PICKUP)
        assertTrue(draft.capture(MAHARAGAMA_PIN), "somebody was waiting for it")

        assertTrue(draft.current.pickupIsChosen)
        assertEquals(MAHARAGAMA_PIN, draft.current.pickup)
        assertNull(draft.current.pickup?.address, "and it still has no name, which is what the pin is for")
    }

    @Test
    fun a_booking_begun_the_way_the_app_begins_one_has_a_pickup(): Unit = runBlocking {
        // **The defect this test exists for** (Δ C097). `begin` takes an OPTIONAL pickup, and every
        // production call site — the home sheet's two and SCR-PA-008's one — omitted it. `refresh()`
        // returns early without one, so SCR-PA-009 loaded neither list: no bus routes, no tiers,
        // nothing to book. The setup above hid it by passing a pickup nothing in the app passed.
        //
        // The fix is inside `BookingDraft` rather than at the three call sites, so a fourth cannot
        // reintroduce it — which is why this asserts the draft rather than a navigation graph.
        lastFix.record(PassengerFix(lat = COLOMBO.lat, lng = COLOMBO.lng))

        draft.begin(dropoff = NUGEGODA)

        assertEquals(COLOMBO.lat, draft.current.pickup?.lat, "a booking with no pickup cannot quote")
        assertTrue(draft.current.isQuotable)

        val model = viewModel()
        val state = model.state.await { it.tiers.isNotEmpty() }
        assertEquals(6, state.tiers.size, "AL-09's six bookable passenger types")
    }

    @Test
    fun a_caller_with_a_better_pickup_than_the_last_fix_keeps_it() = runBlocking {
        // A proxy rider's shared pin and a package's own end are both better answers than where the
        // booker happens to be standing, so an explicit pickup always wins.
        lastFix.record(PassengerFix(lat = COLOMBO.lat, lng = COLOMBO.lng))

        draft.begin(dropoff = COLOMBO, pickup = NUGEGODA)

        assertEquals(NUGEGODA.lat, draft.current.pickup?.lat)
    }

    @Test
    fun a_booking_begun_before_the_map_has_a_fix_is_simply_not_quotable() = runBlocking {
        // Honest rather than clever: on a cold start there is no position to default to, and
        // inventing Colombo Fort would quote a journey from somewhere the passenger is not.
        draft.begin(dropoff = NUGEGODA)

        assertNull(draft.current.pickup)
        assertFalse(draft.current.isQuotable)
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_mode_c_tier_carries_a_price_and_nothing_else() = runBlocking {
        // AL-19 / D5' §BR-23.3: "Before dispatch, Mode C private tiers expose the upfront price
        // only — 'minutes away' and 'distance to driver' are suppressed (no driver matched yet)."
        // `TierQuote` has three fields: the type, the amount, and the token that binds the amount.
        // A card cannot render an ETA it was never given, which is the fence made structural.
        val model = viewModel()
        val state = model.state.await { it.tiers.isNotEmpty() }

        val quote = state.tiers.first()
        assertEquals(RideVehicleType.MOTORBIKE, quote.vehicleType)
        assertTrue(quote.amountMinor > 0)
        assertEquals("token-motorbike", quote.token)

        // The whole surface of the quote, pinned. A Kotlin data class generates exactly one field
        // per constructor property, so a fourth here means something was added that a pre-match
        // card could then show — which is the failure AL-19 exists to prevent. (`$stable` is the
        // Compose compiler's own static field and is not a property of anything.)
        assertEquals(
            listOf("vehicleType", "amountMinor", "token"),
            TierQuote::class.java.declaredFields.map { it.name }.filterNot { it.startsWith("\$") },
            "a tier quote is a price; a new field here is how an ETA reaches a card",
        )
    }

    @Test
    fun every_passenger_tier_is_quoted_once() = runBlocking {
        // Six AL-09 passenger types, one `GET /v1/fare/estimate` each, because the contract takes
        // a single vehicleType and answers a single token — and a token is what a booking carries.
        val model = viewModel()
        model.state.await { it.tiers.isNotEmpty() && !it.tiersLoading }

        assertEquals(
            listOf(
                RideVehicleType.MOTORBIKE,
                RideVehicleType.THREE_WHEELER,
                RideVehicleType.FLEX,
                RideVehicleType.SEDAN,
                RideVehicleType.MINI_VAN,
                RideVehicleType.VAN,
            ),
            bookings.estimated.map { it.first },
        )
        assertTrue(bookings.estimated.all { it.second == FareEstimateKind.PASSENGER })
    }

    @Test
    fun a_tier_whose_price_failed_is_left_off_the_list() = runBlocking {
        // A card with no price is a card a passenger will tap, and tapping it would book a ride at
        // a fare nobody quoted. Better to offer five tiers than six with a hole in one.
        bookings.estimateFails = setOf(RideVehicleType.SEDAN)
        val model = viewModel()

        val state = model.state.await { !it.tiersLoading }

        assertEquals(5, state.tiers.size)
        assertFalse(state.tiers.any { it.vehicleType == RideVehicleType.SEDAN })
    }

    @Test
    fun a_package_is_quoted_on_the_vehicles_its_size_fits() = runBlocking {
        // P-06 made operational. The size hint has already told the sender an L needs a van, so
        // offering them a motorbike would be a driver arriving at a job they cannot do — and
        // truck/mini_truck are delivery-only (AL-09) and appear nowhere else in the app.
        draft.update { it.copy(packageSize = PackageSize.L) }
        val model = viewModel()
        model.setSubject(BookingSubject.PACKAGE)
        model.state.await { it.tiers.isNotEmpty() && !it.tiersLoading }

        val forPackage = bookings.estimated.filter { it.second == FareEstimateKind.PACKAGE }.map { it.first }
        assertEquals(
            listOf(RideVehicleType.VAN, RideVehicleType.MINI_TRUCK, RideVehicleType.TRUCK),
            forPackage,
        )
    }

    @Test
    fun the_gtfs_routes_are_listed_with_their_tag_and_transfer_count() = runBlocking {
        // AL-18 / BR-23.2. Direct options first, then transfer ones — the ordering is the
        // server's and this asserts the client does not resort them.
        bookings.transitAnswer = TransitOptionsResponse(
            options = listOf(direct(), transfer()),
            coverage = TransitCoverage.ACTIVE,
        )
        val model = viewModel()

        val state = model.state.await { it.routes.isNotEmpty() }

        assertEquals(listOf("138", "KV"), state.routes.map { it.routeShortName })
        assertEquals(TransitOptionKind.DIRECT, state.routes.first().kind)
        assertEquals(0, state.routes.first().transfers)
        assertEquals(1, state.routes.last().transfers, "two legs is one change")
        assertEquals("Pettah → Maharagama", state.routes.first().headsign)
    }

    @Test
    fun a_transit_outage_hides_the_bus_section_and_leaves_the_tiers_alone() = runBlocking {
        // AL-55 / D2' §SCR-PA-009: "nothing blocks on GTFS coverage". A feed gap and an
        // unreachable service are the same thing to a passenger — a muted row — and neither
        // touches the private tiers, which are what they can actually book right now.
        bookings.transitFails = true
        val model = viewModel()

        val state = model.state.await { !it.routesLoading && !it.tiersLoading }

        assertTrue(state.publicUnavailable)
        assertTrue(state.routes.isEmpty())
        assertEquals(6, state.tiers.size, "the private tiers are unaffected")
        assertNull(state.error, "a missing feed is not an error the screen reports")
    }

    @Test
    fun no_active_feed_reads_the_same_as_an_outage() = runBlocking {
        bookings.transitAnswer = TransitOptionsResponse(coverage = TransitCoverage.NO_FEED)
        val model = viewModel()

        val state = model.state.await { !it.routesLoading }

        assertTrue(state.publicUnavailable)
    }

    @Test
    fun selecting_a_public_route_draws_it_and_removes_the_fare() = runBlocking {
        // "no fare/payment is charged (public transport)" — so the tier selection is dropped and
        // the CTA becomes Track Route. A map showing a bus route under a Book Now for a tuk-tuk
        // would be two answers to one question.
        bookings.transitAnswer = TransitOptionsResponse(options = listOf(direct()), coverage = TransitCoverage.ACTIVE)
        bookings.routeAnswer = bookings.routeAnswer.copy(
            shape = "_p~iF~ps|U_ulLnnqC",
            nearestStops = listOf(HALT),
        )
        val model = viewModel()
        val loaded = model.state.await { it.tiers.isNotEmpty() && it.routes.isNotEmpty() }
        model.selectTier(loaded.tiers.first())

        model.selectRoute(loaded.routes.first())
        val state = model.state.await { it.routePolyline.isNotEmpty() }

        assertTrue(state.isPublicSelected)
        assertFalse(state.canBook, "a bus is tracked, not booked")
        assertNull(state.draft.vehicleType, "the tier was dropped from the draft")
    }

    @Test
    fun an_off_route_passenger_gets_a_walk_line_and_an_on_route_one_does_not() = runBlocking {
        // "if off-route a blue walking polyline routes to the closest halt (with a 'Walk N m to
        // <halt>' hint)". On-route is the common case and drawing a two-metre line for it would be
        // visual noise, so the hint and the line appear together or not at all.
        bookings.transitAnswer = TransitOptionsResponse(options = listOf(direct()), coverage = TransitCoverage.ACTIVE)
        bookings.routeAnswer = bookings.routeAnswer.copy(nearestStops = listOf(HALT))
        val model = viewModel()
        val loaded = model.state.await { it.routes.isNotEmpty() }

        model.selectRoute(loaded.routes.first())
        val off = model.state.await { it.walkHalt != null }
        assertEquals("Pamankada", off.walkHalt?.haltName)
        assertEquals(2, off.walkPolyline.size, "from the passenger to the halt")

        // A halt at the passenger's own feet: nothing to draw and nothing to say.
        bookings.routeAnswer = bookings.routeAnswer.copy(
            nearestStops = listOf(HALT.copy(lat = COLOMBO.lat, lng = COLOMBO.lng)),
        )
        model.selectRoute(loaded.routes.first())
        val on = model.state.await { it.walkHalt == null }
        assertTrue(on.walkPolyline.isEmpty())
    }

    @Test
    fun booking_sends_the_chosen_tier_its_own_token_and_one_client_request_id() = runBlocking {
        // R-18 dedupes on (passengerId, clientRequestId) and the same value is the idempotency
        // key, so a retry after a timeout returns the ride the first call created. The token is
        // the tier's own: sending another tier's would be `400 invalid-fare-token`.
        val model = viewModel()
        val loaded = model.state.await { it.tiers.size == 6 }
        val sedan = loaded.tiers.single { it.vehicleType == RideVehicleType.SEDAN }

        model.selectTier(sedan)
        model.book()
        model.state.await { it.booked != null }

        val sent = bookings.requested.single()
        assertEquals(RideVehicleType.SEDAN, sent.vehicleType)
        assertEquals("token-sedan", sent.fareEstimateToken)
        assertEquals(CLIENT_REQUEST_ID, sent.clientRequestId)
        assertEquals(RideKind.PASSENGER, sent.kind)
        assertEquals(FakeBookingRepository.RIDE_ID, model.state.value.booked)
    }

    @Test
    fun a_proxy_booking_carries_the_rider_and_says_so() = runBlocking {
        // P-01/P-05 — the driver's offer shows "Third-party booking" with the rider's name and a
        // masked number, and it can only do that if the booking said who the rider was.
        draft.update { it.copy(bookingFor = BookingFor.SOMEONE_ELSE, riderName = "Nimal", riderPhone = "771234567") }
        val model = viewModel()
        val loaded = model.state.await { it.tiers.isNotEmpty() }

        model.selectTier(loaded.tiers.first())
        model.book()
        model.state.await { it.booked != null }

        val sent = bookings.requested.single()
        assertEquals(RideKind.PROXY, sent.kind)
        assertEquals(true, sent.isProxy)
        assertEquals("Nimal", sent.riderName)
        assertEquals("771234567", sent.riderPhone)
    }

    @Test
    fun the_draft_is_thrown_away_the_moment_it_becomes_a_ride() = runBlocking {
        // Otherwise the next booking starts with the last one's rider attached.
        draft.update { it.copy(riderName = "Nimal") }
        val model = viewModel()
        val loaded = model.state.await { it.tiers.isNotEmpty() }

        model.selectTier(loaded.tiers.first())
        model.book()
        model.state.await { it.booked != null }

        assertEquals("", draft.current.riderName)
        assertNull(draft.current.dropoff)
    }

    @Test
    fun a_refused_booking_is_reported_and_leaves_the_draft_intact() = runBlocking {
        // US-6A.10b's `booking-disabled` is the refusal a passenger has genuinely earned. Losing
        // their destination on top of being told they cannot book would be twice punished.
        bookings.requestFails = IllegalStateException("nope")
        val model = viewModel()
        val loaded = model.state.await { it.tiers.isNotEmpty() }

        model.selectTier(loaded.tiers.first())
        model.book()
        val state = model.state.await { it.error != null }

        assertNotNull(draft.current.dropoff, "the destination survives a failed booking")
        assertFalse(state.booking)
        assertNull(state.booked)
    }

    // ------------------------------------------------------------------------------------------

    /**
     * Waits for [predicate] against the fake's recording rather than against state.
     *
     * A re-quote answers the same prices as the first one, so there is no state change to await —
     * what changed is *where it was asked from*, and only the repository saw that.
     */
    private suspend fun awaitCall(what: String, predicate: () -> Boolean) {
        withTimeout(WAIT) {
            while (!predicate()) delay(POLL)
        }
        assertTrue(predicate(), what)
    }

    private fun viewModel() = main.own(RideBookingViewModel(draft = draft, bookings = bookings, keys = keys))

    private fun direct() = TransitOption(
        kind = TransitOptionKind.DIRECT,
        legs = listOf(
            TransitLeg(
                routeId = "R1",
                routeShortName = "138",
                headsign = "Pettah → Maharagama",
                description = "via High Level Rd",
            ),
        ),
    )

    private fun transfer() = TransitOption(
        kind = TransitOptionKind.TRANSIT,
        legs = listOf(
            TransitLeg(routeId = "R2", routeShortName = "KV", headsign = "Kelani Valley Line"),
            TransitLeg(routeId = "R1", routeShortName = "138", headsign = "Pettah → Maharagama"),
        ),
    )

    private companion object {

        /** An unnamed pin two towns from [COLOMBO] — SCR-PA-010b's Map method, as it arrives. */
        val MAHARAGAMA_PIN = Place(lat = 6.8480, lng = 79.9265)

        /** Long enough for a re-quote on a loaded host, short enough to fail rather than hang. */
        val WAIT = 5.seconds
        val POLL = 5.milliseconds

        val COLOMBO = Place(lat = 6.9344, lng = 79.8428, address = "Colombo Fort")
        val NUGEGODA = Place(lat = 6.8649, lng = 79.8997, address = "Nugegoda")

        /** ~1.5 km from the pickup — comfortably past BR-23.2's 400 m halt radius. */
        val HALT = TransitStop(stopId = "S1", name = "Pamankada", lat = 6.9200, lng = 79.8600)

        const val CLIENT_REQUEST_ID = "01JREQ00000000000000000001"
    }
}
