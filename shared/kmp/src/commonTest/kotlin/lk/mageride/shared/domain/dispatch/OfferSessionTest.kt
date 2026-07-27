package lk.mageride.shared.domain.dispatch

import io.ktor.client.engine.mock.MockRequestHandleScope
import io.ktor.client.request.HttpRequestData
import io.ktor.client.request.HttpResponseData
import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.RecordedRequest
import lk.mageride.shared.data.api.respondJson
import lk.mageride.shared.data.api.respondProblem
import lk.mageride.shared.data.api.testApi
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

@OptIn(ExperimentalTime::class)
internal val OFFER_EPOCH: Instant = Instant.parse("2026-07-27T09:00:00Z")

private const val RIDE_ID: Ulid = "01JRIDEOFFERTESTTESTTESTTE"
private const val DRIVER_ID: Ulid = "01JDRIVEROFFERTESTTESTTEST"
private const val OFFER_ID: Ulid = "01JOFFERTESTTESTTESTTESTTE"

private const val ACCEPT_PATH = "/v1/rides/$RIDE_ID/offer/$DRIVER_ID/accept"
private const val DECLINE_PATH = "/v1/rides/$RIDE_ID/offer/$DRIVER_ID/decline"
private const val STATE_PATH = "/v1/rides/$RIDE_ID/state"

private const val RIDE_DETAIL_JSON = """
    {"rideId":"$RIDE_ID","kind":"passenger","state":"Accepted","version":4,
     "pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":6.9,"lng":79.9},
     "vehicleType":"three_wheeler","paymentMethod":"cash","createdAt":"2026-07-27T09:00:00Z"}
"""

private const val ACCEPT_RESPONSE_JSON = """
    {"rideId":"$RIDE_ID","state":"Accepted","version":4,"ride":$RIDE_DETAIL_JSON}
"""

private const val DECLINE_RESPONSE_JSON = """{"rideId":"$RIDE_ID","state":"Matching","version":4}"""
private const val STATE_RESPONSE_JSON = """{"state":"Offered","version":3}"""

/** The harness: a real [OfferSession] over the real C013 pipeline, on a MockEngine. */
private class OfferHarness(val session: OfferSession, val requests: List<RecordedRequest>) {
    fun pathsHit(): List<String> = requests.map { it.path }
}

@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
private fun TestScope.offerHarness(
    respond: suspend MockRequestHandleScope.(Int, HttpRequestData) -> HttpResponseData,
): OfferHarness {
    val test = testApi(respond = respond)
    val clock: () -> Timestamp = { OFFER_EPOCH + testScheduler.currentTime.milliseconds }
    return OfferHarness(OfferSession(api = { test.api.ride }, clock = clock), test.requests)
}

@OptIn(ExperimentalTime::class)
internal fun testOffer(
    expiresAt: Timestamp = OFFER_EPOCH + RideOffer.TTL,
    kind: RideKind = RideKind.PASSENGER,
    version: Int? = null,
    directionalMatched: Boolean = false,
): RideOffer = RideOffer(
    offerId = OFFER_ID,
    rideId = RIDE_ID,
    driverId = DRIVER_ID,
    expiresAt = expiresAt,
    kind = kind,
    fareEstimateMinor = 48_000,
    directionalMatched = directionalMatched,
    version = version,
)

/**
 * The driver's single offer slot: the 15-second window, the two ways of losing it, and what the
 * driver is ready for afterwards (R-02, D5' §3.5/§3.6, ADD Appendix B.2 invariants 2 and 3).
 *
 * These run the real [OfferSession] through the real C013 send pipeline against a MockEngine, for
 * the same reason C014's suites do: the behaviour under test is how the session and the typed
 * errors interact, and a fake `RideApi` would assert the fake.
 */
@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
class OfferSessionTest {

    @Test
    fun a_fresh_session_is_ready_for_an_offer_and_holds_none() = runTest {
        val harness = offerHarness { _, _ -> respondJson("{}") }

        assertEquals(OfferSessionState.Idle, harness.session.state.value)
        assertTrue(harness.session.isReadyForNextOffer)
    }

    @Test
    fun winning_the_accept_hands_over_the_whole_ride_and_ends_the_readiness() = runTest {
        val harness = offerHarness { _, _ -> respondJson(ACCEPT_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = 3))

        val outcome = harness.session.accept()

        val won = assertIs<OfferOutcome.Won>(outcome)
        assertEquals(RIDE_ID, won.ride.rideId)
        assertIs<OfferSessionState.Won>(harness.session.state.value)
        // ADD Appendix B.2 invariant 2: one non-terminal ride per driver. A driver who just won one
        // is not a candidate for the next round.
        assertFalse(harness.session.isReadyForNextOffer)
        assertEquals(listOf(ACCEPT_PATH), harness.pathsHit())
    }

    @Test
    fun an_expired_offer_is_never_sent_and_the_ui_is_told_to_move_on() = runTest {
        val harness = offerHarness { _, _ -> respondJson(ACCEPT_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = 3))

        testScheduler.advanceTimeBy(RideOffer.TTL)

        val outcome = harness.session.accept()

        assertEquals(OfferOutcome.Expired, outcome)
        // The point is the empty list: the accept would have earned a `410` anyway, and the round
        // trip is fifteen seconds of the driver's next offer.
        assertEquals(emptyList(), harness.pathsHit())
        assertTrue(harness.session.isReadyForNextOffer)
    }

    @Test
    fun declining_an_offer_that_has_already_lapsed_is_likewise_not_sent() = runTest {
        val harness = offerHarness { _, _ -> respondJson(DECLINE_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = 3))

        testScheduler.advanceTimeBy(RideOffer.TTL + 1.seconds)

        assertEquals(OfferOutcome.Expired, harness.session.decline())
        assertEquals(emptyList(), harness.pathsHit())
    }

    @Test
    fun a_409_offer_already_accepted_means_somebody_was_faster() = runTest {
        val harness = offerHarness { _, _ ->
            respondProblem(HttpStatusCode.Conflict, "offer-already-accepted")
        }
        harness.session.onOfferPushed(testOffer(version = 3))

        val outcome = harness.session.accept()

        assertEquals(OfferOutcome.Taken, outcome)
        assertTrue(harness.session.isReadyForNextOffer, "a lost race puts the driver straight back in the pool")
    }

    @Test
    fun a_410_offer_expired_means_nobody_took_it_and_is_a_different_outcome() = runTest {
        val harness = offerHarness { _, _ -> respondProblem(HttpStatusCode.Gone, "offer-expired") }
        harness.session.onOfferPushed(testOffer(version = 3))

        val outcome = harness.session.accept()

        // Never collapsed into Taken: one says somebody was faster, the other says nobody was, and
        // a driver app that showed "too slow" for a ride nobody took would misreport their own
        // acceptance rate.
        assertEquals(OfferOutcome.Expired, outcome)
        assertTrue(outcome !== OfferOutcome.Taken)
    }

    @Test
    fun a_409_that_is_not_the_race_is_reported_as_the_failure_it_is() = runTest {
        val harness = offerHarness { _, _ -> respondProblem(HttpStatusCode.Conflict, "version-conflict") }
        harness.session.onOfferPushed(testOffer(version = 3))

        val failed = assertIs<OfferOutcome.Failed>(harness.session.accept())

        assertIs<MageRideError.Conflict>(failed.error)
    }

    @Test
    fun a_402_is_the_daily_fee_gate_and_says_so() = runTest {
        val harness = offerHarness { _, _ ->
            respondProblem(HttpStatusCode.PaymentRequired, "insufficient-wallet")
        }
        harness.session.onOfferPushed(testOffer(version = 3))

        // D-08 / D5' §3.2: the second trip of the day needs the daily fee in the wallet. That is a
        // balance the driver can top up, not a dispatch failure.
        assertEquals(OfferOutcome.WalletBlocked, harness.session.accept())
        assertTrue(harness.session.isReadyForNextOffer)
    }

    @Test
    fun declining_releases_the_slot_without_penalty() = runTest {
        val harness = offerHarness { _, _ -> respondJson(DECLINE_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = 3))

        assertEquals(OfferOutcome.Declined, harness.session.decline())
        assertEquals(OfferSessionState.Idle, harness.session.state.value)
        assertEquals(listOf(DECLINE_PATH), harness.pathsHit())
    }

    @Test
    fun a_network_failure_still_frees_the_driver_for_the_next_round() = runTest {
        val harness = offerHarness { _, _ -> respondProblem(HttpStatusCode.InternalServerError, "internal-error") }
        harness.session.onOfferPushed(testOffer(version = 3))

        assertIs<OfferOutcome.Failed>(harness.session.decline())
        // The offer's own TTL releases the driver server-side fifteen seconds later either way;
        // holding the local slot open for a call that already failed would cost them the round.
        assertTrue(harness.session.isReadyForNextOffer)
    }

    @Test
    fun the_ride_version_is_fetched_when_the_offer_envelope_did_not_carry_one() = runTest {
        val harness = offerHarness { _, request ->
            if (request.url.encodedPath == STATE_PATH) {
                respondJson(STATE_RESPONSE_JSON)
            } else {
                respondJson(ACCEPT_RESPONSE_JSON)
            }
        }
        // `dispatch.events` `offer.created` (D6' §2.2) has no `version`, and
        // `AcceptRideOfferRequest` requires one (R-14). One extra read, inside the fifteen seconds.
        harness.session.onOfferPushed(testOffer(version = null))

        assertIs<OfferOutcome.Won>(harness.session.accept())
        assertEquals(listOf(STATE_PATH, ACCEPT_PATH), harness.pathsHit())
        assertTrue(harness.requests.last().body.contains(""""version":3"""))
    }

    @Test
    fun a_known_version_is_used_without_the_extra_read() = runTest {
        val harness = offerHarness { _, _ -> respondJson(ACCEPT_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = null))
        harness.session.onVersionKnown(3)

        assertIs<OfferOutcome.Won>(harness.session.accept())
        assertEquals(listOf(ACCEPT_PATH), harness.pathsHit())
    }

    @Test
    fun the_accept_echoes_the_offer_id_the_push_carried() = runTest {
        val harness = offerHarness { _, _ -> respondJson(ACCEPT_RESPONSE_JSON) }
        harness.session.onOfferPushed(testOffer(version = 3))

        harness.session.accept()

        // The server's conditional UPDATE guards on `current_offer_id = :offerId` (D5' §6.1); an
        // accept quoting the wrong offer is how a driver wins a ride they were never offered.
        assertTrue(harness.requests.single().body.contains(""""offerId":"$OFFER_ID""""))
    }

    @Test
    fun a_second_offer_replaces_the_first_because_a_driver_holds_only_one() = runTest {
        val harness = offerHarness { _, _ -> respondJson(ACCEPT_RESPONSE_JSON) }
        val second = testOffer(expiresAt = OFFER_EPOCH + 30.seconds, version = 5)

        harness.session.onOfferPushed(testOffer(version = 3))
        harness.session.onOfferPushed(second)

        // `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')` plus the Redis reservation
        // lock (D5' §3.6) mean dispatch cannot have reserved this driver twice — so a second
        // arrival means the first is already dead.
        assertEquals(OfferSessionState.Live(second), harness.session.state.value)
    }

    @Test
    fun the_countdown_runs_the_fifteen_seconds_down_to_zero() = runTest {
        val harness = offerHarness { _, _ -> respondJson("{}") }
        harness.session.onOfferPushed(testOffer())

        val ticks = harness.session.countdown(interval = 1.seconds).toList()

        assertEquals(RideOffer.TTL, ticks.first())
        assertEquals(Duration.ZERO, ticks.last())
        assertEquals(16, ticks.size, "fifteen one-second ticks plus the zero that ends it")
    }

    @Test
    fun the_countdown_of_an_offer_that_is_already_gone_ends_immediately() = runTest {
        val harness = offerHarness { _, _ -> respondJson("{}") }

        assertEquals(listOf(Duration.ZERO), harness.session.countdown().toList())
    }

    @Test
    fun the_local_expiry_drops_the_offer_without_telling_the_server() = runTest {
        val harness = offerHarness { _, _ -> respondJson("{}") }
        harness.session.onOfferPushed(testOffer())

        harness.session.onExpired()

        assertEquals(OfferSessionState.Idle, harness.session.state.value)
        assertEquals(emptyList(), harness.pathsHit())
    }

    @Test
    fun the_progress_ring_is_measured_from_the_deadline_not_from_arrival() = runTest {
        // A push that took two seconds to arrive should show thirteen seconds of ring, not fifteen.
        val offer = testOffer(expiresAt = OFFER_EPOCH + 13.seconds)

        assertEquals(13.0 / 15.0, offer.progress(OFFER_EPOCH), absoluteTolerance = 1e-9)
        assertEquals(0.0, offer.progress(OFFER_EPOCH + 20.seconds), absoluteTolerance = 1e-9)
        assertEquals(Duration.ZERO, offer.remaining(OFFER_EPOCH + 20.seconds))
        assertTrue(offer.isExpired(OFFER_EPOCH + 13.seconds), "the boundary belongs to the expiry")
    }
}
