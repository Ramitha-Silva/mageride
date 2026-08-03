package lk.mageride.driver.home

import lk.mageride.shared.domain.dispatch.RideOffer
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * The `RIDE_OFFER` push, turned into the offer the fifteen seconds run on.
 *
 * The payload asserted here is notification-svc's own — `EventHandlers.OfferAsync` writes `kind`,
 * `offerId`, `rideId`, `expiresAt` and a **rendered** `fare`, and nothing else. Everything the
 * badges need is on the ride, which is why [OfferViewModel] reads it.
 */
@OptIn(ExperimentalTime::class)
class OfferInboxTest {

    @Test
    fun the_offer_is_built_from_the_ids_and_the_servers_own_deadline() {
        val offer = OfferInbox.offerFrom(
            data = mapOf(
                "kind" to "ride_offer",
                "offerId" to OFFER_ID,
                "rideId" to Fixtures.RIDE_ID,
                "expiresAt" to (Fixtures.NOW + 9.seconds).toString(),
                "fare" to "1,240.00",
            ),
            driverId = Fixtures.DRIVER_ID,
            now = Fixtures.NOW,
        )

        requireNotNull(offer)
        assertEquals(OFFER_ID, offer.offerId)
        assertEquals(Fixtures.RIDE_ID, offer.rideId)
        assertEquals(Fixtures.DRIVER_ID, offer.driverId)
        // The deadline is ride-svc's, not this device's: a push that took six seconds to arrive
        // must show nine seconds of ring rather than a fresh fifteen.
        assertEquals(9.seconds, offer.remaining(Fixtures.NOW))
        assertEquals(124_000L, offer.fareEstimateMinor, "Rs 1,240.00 is 124000 minor units")
    }

    @Test
    fun an_envelope_with_no_deadline_falls_back_to_exactly_fifteen_seconds() {
        // US-6A.3 / D5' §3.5. Fifteen is the TTL Redis PEXPIREs and the Quartz backstop fires on,
        // so it is also the only honest guess when the envelope did not carry the deadline.
        val offer = OfferInbox.offerFrom(
            data = mapOf("offerId" to OFFER_ID, "rideId" to Fixtures.RIDE_ID),
            driverId = Fixtures.DRIVER_ID,
            now = Fixtures.NOW,
        )

        requireNotNull(offer)
        assertEquals(15.seconds, RideOffer.TTL, "the window US-6A.3 fixes")
        assertEquals(Fixtures.NOW + RideOffer.TTL, offer.expiresAt)
        assertEquals(RideOffer.TTL, offer.remaining(Fixtures.NOW))
        assertEquals(Duration_ZERO, offer.remaining(Fixtures.NOW + RideOffer.TTL), "expired at exactly 15 s")
    }

    @Test
    fun an_envelope_without_the_two_ids_is_not_an_offer() {
        // Nothing can be accepted without a ride and an offer id, and a takeover that cannot be
        // accepted is fifteen seconds of a driver's attention for nothing.
        assertNull(
            OfferInbox.offerFrom(mapOf("rideId" to Fixtures.RIDE_ID), Fixtures.DRIVER_ID, Fixtures.NOW),
            "no offerId",
        )
        assertNull(
            OfferInbox.offerFrom(mapOf("offerId" to OFFER_ID), Fixtures.DRIVER_ID, Fixtures.NOW),
            "no rideId",
        )
    }

    @Test
    fun a_rendered_fare_is_parsed_as_integers_and_never_through_a_double() {
        // C012's fence: money is `Long` minor units, never `Double`. notification-svc formats the
        // fare for the SMS fallback and puts the same string on the push, so this is the one place
        // in the app that has to read money back out of copy.
        assertEquals(124_000L, OfferInbox.parseRupees("1,240.00"))
        assertEquals(48_000L, OfferInbox.parseRupees("480"))
        assertEquals(48_050L, OfferInbox.parseRupees("480.5"), "one decimal digit is tens of cents")
        assertEquals(-5_000L, OfferInbox.parseRupees("-50.00"))
        assertNull(OfferInbox.parseRupees(null))
        assertNull(OfferInbox.parseRupees(""))
        assertNull(OfferInbox.parseRupees("Rs 480"), "a prefixed value is not a number this reads")
        assertNull(OfferInbox.parseRupees("1.2.3"))
    }

    private companion object {
        const val OFFER_ID = "01JOFFER00000000000000000"
        val Duration_ZERO = kotlin.time.Duration.ZERO
    }
}
