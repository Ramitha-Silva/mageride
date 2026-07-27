package lk.mageride.shared.domain.dispatch

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.PhoneMasked
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * An offer as it reaches the driver's device (`dispatch.events` `offer.created`, D6' §2.2).
 *
 * It arrives twice over: as a high-priority FCM `RIDE_OFFER` push when the app is backgrounded
 * (E-01) and over MQTT when it is not. Either way the payload is this one — dispatch-svc writes it
 * through the transactional outbox and it is published **only after the DB commit**, so an offer a
 * driver can see is an offer that exists (R-13).
 *
 * **Fifteen seconds** (US-6A.3, D5' §3.5). Redis `PEXPIRE 15000` is the fast hint and a clustered
 * Quartz job is the durable backstop (R-04), so the offer dies on time whether or not Redis is
 * healthy. [expiresAt] is the deadline both of them are aimed at.
 *
 * @property offerId The `dispatch.offers` row. Echoed on accept and decline.
 * @property rideId The ride being offered.
 * @property driverId Who it was offered to. A driver holds at most one live offer at a time
 *   (ADD Appendix B.2 invariant 3, D5' §3.6).
 * @property expiresAt The 15-second deadline.
 * @property kind Booking kind, so the sheet can be the right one before anything is fetched.
 * @property isProxy Third-party booking (P-05) — the driver app badges it.
 * @property riderName Who is actually travelling, on a proxy booking.
 * @property riderPhoneMasked The rider's masked number. The clear one arrives with the ride, from
 *   `Accepted` onward (AL-48).
 * @property packageSize Package bookings only (P-06). Shown even when the vehicle is compatible,
 *   because P-11 filters candidates but never overrides a driver's own judgement.
 * @property packageDescription The sender's contents note.
 * @property directionalMatched Whether this offer came through the driver's own Destination Filter
 *   (DT-08) — the badge that tells them the filter is working.
 * @property fareEstimateMinor The quoted fare, integer minor units.
 * @property currency Always LKR.
 * @property paymentMethod How the passenger chose to pay.
 * @property pickup Where to collect, when the envelope carried it.
 * @property dropoff Where to drop, when the envelope carried it.
 * @property version The ride's optimistic-concurrency version, which
 *   `AcceptRideOfferRequest` requires. **The `offer.created` envelope does not carry one** — see
 *   [OfferSession.accept], which reads it from `GET /v1/rides/{rideId}/state` when it is absent.
 */
@Serializable
public data class RideOffer(
    val offerId: Ulid,
    val rideId: Ulid,
    val driverId: Ulid,
    val expiresAt: Timestamp,
    val kind: RideKind = RideKind.PASSENGER,
    val isProxy: Boolean = false,
    val riderName: String? = null,
    val riderPhoneMasked: PhoneMasked? = null,
    val packageSize: PackageSize? = null,
    val packageDescription: String? = null,
    val directionalMatched: Boolean = false,
    val fareEstimateMinor: Long,
    @SerialName("currency")
    val currency: Currency = Currency.LKR,
    val paymentMethod: RidePaymentMethod = RidePaymentMethod.CASH,
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val version: RideVersion? = null,
) : MoneyHolder {

    override val money: Money get() = Money(amountMinor = fareEstimateMinor, currency = currency)

    /** Whether the fifteen seconds have run out. */
    public fun isExpired(now: Timestamp): Boolean = now >= expiresAt

    /** What is left of the countdown, floored at zero. */
    public fun remaining(now: Timestamp): Duration {
        val left = expiresAt - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    /**
     * The countdown as `1.0` at the moment of the offer down to `0.0` at the deadline, for a ring
     * or a bar.
     *
     * Derived from [expiresAt] and the fixed [TTL] rather than from when this device happened to
     * see the offer: a push that took two seconds to arrive should show thirteen seconds of ring,
     * not fifteen.
     */
    public fun progress(now: Timestamp, ttl: Duration = TTL): Double = (remaining(now) / ttl).coerceIn(0.0, 1.0)

    public companion object {

        /** The offer window (US-6A.3, D5' §3.5). Fifteen seconds, then the cascade moves on. */
        public val TTL: Duration = 15.seconds
    }
}
