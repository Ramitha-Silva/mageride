package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

// Proxy booking — someone books a ride for someone else (P-01..P-05, P-12, P-13, D5' §10).
//
// The aggregate is unchanged: a `proxy` ride traverses exactly the states a `passenger` ride does
// (ADD Appendix B.2 invariant 6). Three things differ, and all three are here:
//
//   * the booker is not the rider, and the rider may have no account at all (P-01/P-03);
//   * the booker can ask the rider where they are, over a durable 5-minute round trip (P-02/P-13);
//   * who pays depends on how (P-04).
//
// The rider's phone number is hashed at rest server-side (P-03). Nothing in this file stores one.

/** Who settles the fare on a proxy booking (P-04, US-8.21). */
public enum class ProxyPayer {

    /**
     * The rider pays the driver directly, in cash.
     *
     * The rider is told over FCM that they are expected to (US-8.21) — a proxy booking whose
     * rider does not know they are paying is how a driver ends up unpaid at a kerb.
     */
    RIDER,

    /** The booker pays, through LankaQR or OnePay. They made the booking and they hold the card. */
    BOOKER,
}

/**
 * The rules that make a proxy booking different from an ordinary one.
 *
 * Everything else about it — dispatch, fare, daily fee, the state machine — is identical, which is
 * the point of P-01 being an invariant rather than a second aggregate.
 */
public object ProxyBooking {

    /** How long the rider has to answer a location request (P-02: `const: 300` in the contract). */
    public val LOCATION_REQUEST_TTL: Duration = CreateLocationRequestResponse.TTL_SECONDS.seconds

    /** Location requests one booker may make per hour (P-12, Redis token bucket). */
    public const val REQUESTS_PER_HOUR: Int = 5

    /** Location requests one booker may make per Asia/Colombo day (P-12). */
    public const val REQUESTS_PER_DAY: Int = 30

    /**
     * Who pays, for a booking made with [method] (P-04).
     *
     * `cod` never appears on a proxy booking — it is package-only (`rides.rides.payment_method`
     * CHECK, C004) — and is treated as the cash case it is.
     */
    public fun payerFor(method: RidePaymentMethod): ProxyPayer = when (method) {
        RidePaymentMethod.CASH, RidePaymentMethod.COD -> ProxyPayer.RIDER
        RidePaymentMethod.LANKAQR, RidePaymentMethod.ONEPAY -> ProxyPayer.BOOKER
    }

    /**
     * Who the driver's call button reaches (P-05).
     *
     * Always the **rider**, never the booker: the driver is going to a kerb to collect a person,
     * and the person who booked the ride may be in another district. `RideDetail.counterpartyPhone`
     * carries that number from `Accepted` onward and is already the rider's (AL-48) — this exists
     * to say so where the rule is, rather than leaving it implied by a field name.
     */
    public const val DRIVER_CALLS_THE_RIDER: Boolean = true
}

/**
 * A booker's "where are you?" round trip, projected (P-02, P-13).
 *
 * The live path is the SignalR group `booker:{bookerId}:loc-req:{requestId}`; the REST read exists
 * for reconnect and support. Either way this is what the waiting screen renders.
 *
 * @property requestId The request, and the SignalR group suffix.
 * @property state Where it has got to.
 * @property geo The confirmed pickup point.
 * @property expiresAt When the five minutes are up.
 */
public data class LocationRequestProjection(
    val requestId: String,
    val state: LocationRequestState,
    val geo: GeoPointWithAccuracy?,
    val expiresAt: Timestamp,
) {
    init {
        // P-02: "Decline never leaks GPS." A declined or expired request carries no coordinates at
        // all — not a coarse one, not a stale one. A projection that let one through would put a
        // pin on the booker's map for a rider who said no.
        require(geo == null || state == LocationRequestState.Confirmed) {
            "a location request in state $state must carry no coordinates"
        }
    }

    /** Whether the request has resolved, one way or another. */
    public val isResolved: Boolean get() = state != LocationRequestState.Pending

    /**
     * Whether the rider has no MageRide account (P-03, AL-45).
     *
     * Not a failure: they get an SMS with a `pickup_confirm` web token and resolve through
     * public-bff instead of in-app. The booker's screen says "waiting", not "no such user".
     */
    public val isUnregisteredRider: Boolean get() = state == LocationRequestState.RiderNotRegistered

    /** What is left of the five minutes, floored at zero. */
    public fun remaining(now: Timestamp): Duration {
        val left = expiresAt - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    /**
     * Whether the TTL has run out on this device.
     *
     * The durable Quartz timer is what actually expires it (P-02); this is the countdown the
     * booker watches, and it should stop at zero rather than wait for the frame.
     */
    public fun hasLapsed(now: Timestamp): Boolean =
        state == LocationRequestState.Pending && remaining(now) == Duration.ZERO

    public companion object {

        /** Projects the REST read. */
        public fun of(request: LocationRequest): LocationRequestProjection = LocationRequestProjection(
            requestId = request.requestId,
            state = request.state,
            geo = request.geo,
            expiresAt = request.expiresAt,
        )
    }
}

/**
 * The P-12 abuse budget, client side.
 *
 * The Redis token bucket is authoritative and answers `429 loc-request-rate-limited`; this exists
 * so the booker's screen can grey the button out and say why, instead of spending a request to
 * find out. Repeated **declines** are a separate signal — they are logged to
 * `safety.location_request_audit` and raise a reputation flag — and are deliberately not counted
 * here: a client must not be able to tell a booker how close they are to being flagged.
 *
 * @property inLastHour Requests this booker has made in the trailing hour.
 * @property today Requests made so far this Asia/Colombo day (D-38).
 */
public data class LocationRequestBudget(val inLastHour: Int = 0, val today: Int = 0) {

    /** Requests left this hour. */
    public val hourlyRemaining: Int get() = (ProxyBooking.REQUESTS_PER_HOUR - inLastHour).coerceAtLeast(0)

    /** Requests left today. */
    public val dailyRemaining: Int get() = (ProxyBooking.REQUESTS_PER_DAY - today).coerceAtLeast(0)

    /** Whether another request is worth sending. */
    public val canRequest: Boolean get() = hourlyRemaining > 0 && dailyRemaining > 0

    /** The budget after one more request goes out. */
    public fun spent(): LocationRequestBudget = copy(inLastHour = inLastHour + 1, today = today + 1)
}
