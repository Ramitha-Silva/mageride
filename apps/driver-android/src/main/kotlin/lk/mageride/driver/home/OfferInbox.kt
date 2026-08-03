package lk.mageride.driver.home

import lk.mageride.driver.push.PushMessage
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import lk.mageride.shared.domain.dispatch.OfferSession
import lk.mageride.shared.domain.dispatch.RideOffer
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * Where a `RIDE_OFFER` push becomes the driver's live offer.
 *
 * **The push is the delivery, not the payload.** notification-svc's `offer.created` handler
 * (`EventHandlers.OfferAsync`) writes exactly five data values — `kind`, `offerId`, `rideId`,
 * `expiresAt` and a **rendered** `fare`/`distance` pair that exists because the SMS fallback
 * interpolates the same dictionary. That is enough to start the fifteen-second clock and not enough
 * to draw SCR-DA-014, so [OfferViewModel] reads `GET /v1/rides/{rideId}` for the badges, the
 * pickup and the drop — inside the window, once, and only because the envelope cannot carry them.
 *
 * **A backgrounded app has no socket** (`signalr-hub.md` §6), so this path is not a nicety: it is
 * the only delivery there is until the app is in front of the driver. `DriverMessagingService`
 * feeds it, which is why this is a process singleton rather than a view model's.
 */
@OptIn(ExperimentalTime::class)
internal class OfferInbox(private val offers: OfferSession, private val sessions: AuthSessionManager) {

    /**
     * Takes delivery of a push. Anything that is not an offer, or arrives signed out, is dropped.
     *
     * `OfferSession.onOfferPushed` **replaces** whatever was held, which is correct rather than
     * lossy: a second offer reaching this device means the first is already dead — dispatch cannot
     * have reserved one driver twice (ADD Appendix B.2 invariant 3).
     */
    @Suppress("ReturnCount") // One guard per way a push can fail to be this driver's live offer.
    fun onPush(message: PushMessage) {
        if (message.kind != PushMessage.KIND_RIDE_OFFER) return
        val driverId = (sessions.state.value as? SessionState.SignedIn)?.userId ?: return
        val offer = offerFrom(message.data, driverId, Clock.System.now()) ?: return
        offers.onOfferPushed(offer)
    }

    internal companion object {

        /** `data.offerId` — the `dispatch.offers` row, echoed on accept and decline. */
        const val KEY_OFFER_ID: String = "offerId"

        /** `data.rideId`. Absent only on a malformed envelope; without it nothing can be accepted. */
        const val KEY_RIDE_ID: String = "rideId"

        /** `data.expiresAt` — ride-svc's own deadline, round-tripped as ISO-8601. */
        const val KEY_EXPIRES_AT: String = "expiresAt"

        /** `data.fare` — **rendered rupees**, e.g. `1,240.00`. See [parseRupees]. */
        const val KEY_FARE: String = "fare"

        /**
         * Builds the offer the countdown runs on.
         *
         * [now] is what the fifteen seconds are measured from **only when the envelope carried no
         * deadline**. When it did, that deadline wins: a push that took two seconds to arrive
         * should show thirteen seconds of ring, not fifteen, and `RideOffer.progress` derives
         * exactly that from `expiresAt` against the fixed TTL.
         */
        @Suppress("ReturnCount") // The two ids are both required; neither can be defaulted.
        fun offerFrom(data: Map<String, String>, driverId: Ulid, now: Timestamp): RideOffer? {
            val offerId = data[KEY_OFFER_ID]?.takeIf(String::isNotBlank) ?: return null
            val rideId = data[KEY_RIDE_ID]?.takeIf(String::isNotBlank) ?: return null

            return RideOffer(
                offerId = offerId,
                rideId = rideId,
                driverId = driverId,
                expiresAt = parseInstant(data[KEY_EXPIRES_AT]) ?: (now + RideOffer.TTL),
                fareEstimateMinor = parseRupees(data[KEY_FARE]) ?: 0L,
            )
        }

        /** A `RIDE_OFFER` push's `expiresAt`, or `null` for a value this build cannot read. */
        private fun parseInstant(value: String?): Timestamp? {
            val text = value?.takeIf(String::isNotBlank) ?: return null
            return runCatching { Timestamp.parse(text) }.getOrNull()
        }

        /**
         * `1,240.00` back into `124000` minor units — **exactly**, with no `Double` anywhere.
         *
         * notification-svc formats the fare for the SMS fallback and puts the same string on the
         * push, so the only number available before the ride is read is a rendered one. Parsing it
         * through a floating-point type is the bug C012's "money is `Long` minor units, never
         * `Double`" fence exists to prevent, so the rupees and the cents are parsed as separate
         * integers. A value this cannot read answers `null`, and the ride read fills it in.
         */
        @Suppress("ReturnCount") // One guard per way a rendered string can fail to be money.
        fun parseRupees(value: String?): Long? {
            val cleaned = value?.replace(",", "")?.trim()?.takeIf(String::isNotEmpty) ?: return null
            val negative = cleaned.startsWith("-")
            val parts = cleaned.removePrefix("-").split('.')
            if (parts.size > 2) return null

            val rupees = parts[0].toLongOrNull() ?: return null
            val cents = when (parts.size) {
                1 -> 0L
                else -> parts[1].padEnd(MINOR_DIGITS, '0').take(MINOR_DIGITS).toLongOrNull() ?: return null
            }

            val minor = rupees * MINOR_UNITS + cents
            return if (negative) -minor else minor
        }

        /** Rs 1 is 100 minor units (CLAUDE.md, D3' §0). */
        private const val MINOR_UNITS = 100L
        private const val MINOR_DIGITS = 2
    }
}
