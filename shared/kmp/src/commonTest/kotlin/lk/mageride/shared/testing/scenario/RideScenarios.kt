package lk.mageride.shared.testing.scenario

import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.ride.FareEstimate
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideDriver
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.domain.ride.RideTrigger
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Duration.Companion.seconds

// The canonical journeys. Everything here is hand-written rather than synthesised, because a
// scenario's whole value is that it is *coherent*: the driver appears at Accepted and not before
// (AL-48), the fare is the same number in the estimate and the settlement, the package's status
// tracks its OTPs, and the version increments once per server write. DtoFixtures can populate a
// RideDetail; it cannot make one that tells the truth.

/** The offer TTL — R-02 / D5' §11.11. Fifteen seconds, not a round number someone chose. */
private val OFFER_TTL = 15.seconds

private val ESTIMATE = FareEstimate(
    amountMinor = Fixtures.FARE.amountMinor,
    currency = Currency.LKR,
    surchargeMinor = 0L,
)

private val ASSIGNED_DRIVER = RideDriver(
    driverId = Fixtures.DRIVER_ID,
    name = "S. Fernando",
    photoUrl = Fixtures.ASSET_URL,
    vehicleType = null,
    registrationNumber = "WP CAB-1234",
    rating = 4.8,
    etaSeconds = 240,
)

/**
 * A passenger books a ride for themselves, is matched, rides, and pays by card.
 *
 * The happy path down ADD Appendix B.2 and the one every other scenario is a variation on:
 * `Requested → Matching → Offered → Accepted → DriverArrived → InProgress → Completed →
 * PaymentPending → Paid`. Nine states, eight edges, each of them the only edge the table draws
 * between its two endpoints — so a projection driven through this can name every trigger back.
 */
public val ModeCRide: RideScenario = RideScenario(
    name = "Mode C ride, paid by card",
    kind = RideKind.PASSENGER,
    request = RideRequest(
        clientRequestId = Fixtures.CLIENT_REQUEST_ID,
        kind = RideKind.PASSENGER,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.THREE_WHEELER,
        fareEstimateToken = Fixtures.FARE_ESTIMATE_TOKEN,
        paymentMethod = RidePaymentMethod.ONEPAY,
    ),
    booked = RequestRideResponse(
        rideId = Fixtures.RIDE_ID,
        state = RideState.Requested,
        version = 1,
        pickupOtp = Fixtures.OTP,
        estimatedFare = ESTIMATE,
    ),
    steps = listOf(
        RideStep(RideState.Matching, version = 2, trigger = RideTrigger.DISPATCH_STARTED),
        RideStep(RideState.Offered, version = 3, trigger = RideTrigger.OFFER_PUSHED, Fixtures.NOW + OFFER_TTL),
        RideStep(RideState.Accepted, version = 4, trigger = RideTrigger.OFFER_ACCEPTED),
        RideStep(RideState.DriverArrived, version = 5, trigger = RideTrigger.DRIVER_ARRIVED),
        RideStep(RideState.InProgress, version = 6, trigger = RideTrigger.RIDE_STARTED),
        RideStep(RideState.Completed, version = 7, trigger = RideTrigger.RIDE_COMPLETED),
        RideStep(RideState.PaymentPending, version = 8, trigger = RideTrigger.FARE_FINALISED),
        RideStep(RideState.Paid, version = 9, trigger = RideTrigger.PAYMENT_SUCCEEDED),
    ),
    detailFor = ::passengerDetail,
)

/**
 * A booker orders a ride for someone else, who pays the driver in cash (P-01, P-03, P-05).
 *
 * The same nine states — the aggregate is kind-agnostic — with three differences that are the
 * whole point of the scenario: `bookerId` and `riderId` are different people, the rider is
 * identified by a name and a number rather than by an account, and the counterparty phone the
 * driver is shown is the **rider's**, never the booker's (P-05, AL-48). It settles as
 * `CashSettled` because a proxy booking is the cash case.
 */
public val ProxyRide: RideScenario = RideScenario(
    name = "Proxy ride, settled in cash",
    kind = RideKind.PROXY,
    request = RideRequest(
        clientRequestId = Fixtures.CLIENT_REQUEST_ID,
        kind = RideKind.PROXY,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.THREE_WHEELER,
        fareEstimateToken = Fixtures.FARE_ESTIMATE_TOKEN,
        paymentMethod = RidePaymentMethod.CASH,
        isProxy = true,
        riderName = "K. Silva",
        riderPhone = Fixtures.DRIVER_PHONE,
    ),
    booked = RequestRideResponse(
        rideId = Fixtures.RIDE_ID,
        state = RideState.Requested,
        version = 1,
        pickupOtp = Fixtures.OTP,
        estimatedFare = ESTIMATE,
    ),
    steps = listOf(
        RideStep(RideState.Matching, version = 2, trigger = RideTrigger.DISPATCH_STARTED),
        RideStep(RideState.Offered, version = 3, trigger = RideTrigger.OFFER_PUSHED, Fixtures.NOW + OFFER_TTL),
        RideStep(RideState.Accepted, version = 4, trigger = RideTrigger.OFFER_ACCEPTED),
        RideStep(RideState.DriverArrived, version = 5, trigger = RideTrigger.DRIVER_ARRIVED),
        RideStep(RideState.InProgress, version = 6, trigger = RideTrigger.RIDE_STARTED),
        RideStep(RideState.Completed, version = 7, trigger = RideTrigger.RIDE_COMPLETED),
        RideStep(RideState.PaymentPending, version = 8, trigger = RideTrigger.FARE_FINALISED),
        RideStep(RideState.CashSettled, version = 9, trigger = RideTrigger.CASH_SETTLED),
    ),
    detailFor = ::proxyDetail,
)

/**
 * A package is sent across town and paid for on delivery (P-07, P-08, AL-21, AL-33).
 *
 * Two OTP handoffs rather than one: the sender's releases the package at pickup, the recipient's
 * accepts it at delivery — `PackageHandoff` (C015) is what gates `COMPLETE` on them. Settlement is
 * `CashOnDeliveryCollected`, the terminal state only a package can reach, and `packageStatus`
 * tracks the handoff beside the kind-agnostic ride state rather than inside it.
 */
public val PackageDelivery: RideScenario = RideScenario(
    name = "Package delivery, cash on delivery",
    kind = RideKind.PACKAGE,
    request = RideRequest(
        clientRequestId = Fixtures.CLIENT_REQUEST_ID,
        kind = RideKind.PACKAGE,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.MOTORBIKE,
        fareEstimateToken = Fixtures.FARE_ESTIMATE_TOKEN,
        paymentMethod = RidePaymentMethod.COD,
        packageSize = PackageSize.M,
        packageDescription = "Documents, do not fold",
        recipientName = "N. Jayasuriya",
        recipientPhone = Fixtures.DRIVER_PHONE,
    ),
    booked = RequestRideResponse(
        rideId = Fixtures.RIDE_ID,
        state = RideState.Requested,
        version = 1,
        pickupOtp = Fixtures.OTP,
        estimatedFare = ESTIMATE,
    ),
    steps = listOf(
        RideStep(RideState.Matching, version = 2, trigger = RideTrigger.DISPATCH_STARTED),
        RideStep(RideState.Offered, version = 3, trigger = RideTrigger.OFFER_PUSHED, Fixtures.NOW + OFFER_TTL),
        RideStep(RideState.Accepted, version = 4, trigger = RideTrigger.OFFER_ACCEPTED),
        RideStep(RideState.DriverArrived, version = 5, trigger = RideTrigger.DRIVER_ARRIVED),
        RideStep(RideState.InProgress, version = 6, trigger = RideTrigger.RIDE_STARTED),
        RideStep(RideState.Completed, version = 7, trigger = RideTrigger.RIDE_COMPLETED),
        RideStep(RideState.PaymentPending, version = 8, trigger = RideTrigger.FARE_FINALISED),
        RideStep(RideState.CashOnDeliveryCollected, version = 9, trigger = RideTrigger.COD_COLLECTED),
    ),
    detailFor = ::packageDetail,
)

/** Every ride journey the platform supports, for a test that wants to sweep all of them. */
public val RideScenarios: List<RideScenario> = listOf(ModeCRide, ProxyRide, PackageDelivery)

// ---- the aggregates, as they read at each step -------------------------------------------------

private fun passengerDetail(state: RideState, version: RideVersion): RideDetail = RideDetail(
    rideId = Fixtures.RIDE_ID,
    kind = RideKind.PASSENGER,
    state = state,
    version = version,
    bookerId = Fixtures.PASSENGER_ID,
    riderId = Fixtures.PASSENGER_ID,
    pickup = Fixtures.PICKUP,
    dropoff = Fixtures.DROPOFF,
    vehicleType = RideVehicleType.THREE_WHEELER,
    paymentMethod = RidePaymentMethod.ONEPAY,
    offerExpiresAt = (Fixtures.NOW + OFFER_TTL).takeIf { state == RideState.Offered },
    // AL-48: a counterparty exists from Accepted onward and not one frame earlier.
    driver = ASSIGNED_DRIVER.takeIf { state.isDriverAssigned },
    counterpartyPhone = Fixtures.DRIVER_PHONE.takeIf { state.isDriverAssigned },
    fare = ESTIMATE,
    createdAt = Fixtures.NOW,
)

private fun proxyDetail(state: RideState, version: RideVersion): RideDetail = passengerDetail(state, version).copy(
    kind = RideKind.PROXY,
    // P-01: the booker and the rider are different people, and the rider may have no account.
    bookerId = Fixtures.PASSENGER_ID,
    riderId = null,
    riderName = "K. Silva",
    paymentMethod = RidePaymentMethod.CASH,
    // P-05: what the driver is shown is the rider's number, never the booker's.
    counterpartyPhone = Fixtures.DRIVER_PHONE.takeIf { state.isDriverAssigned },
)

private fun packageDetail(state: RideState, version: RideVersion): RideDetail = passengerDetail(state, version).copy(
    kind = RideKind.PACKAGE,
    riderId = null,
    vehicleType = RideVehicleType.MOTORBIKE,
    paymentMethod = RidePaymentMethod.COD,
    packageSize = PackageSize.M,
    packageDescription = "Documents, do not fold",
    packageStatus = packageStatusAt(state),
    recipientName = "N. Jayasuriya",
    // AL-33's sheets draw a call button beside each end of the delivery, so both numbers appear
    // together and on the same terms as `counterpartyPhone` — from Accepted onward, never before.
    senderPhone = Fixtures.PASSENGER_PHONE.takeIf { state.isDriverAssigned },
    recipientPhone = Fixtures.DRIVER_PHONE.takeIf { state.isDriverAssigned },
)

/** Handoff progress runs beside the ride state, so it has to be derived from it. */
private fun packageStatusAt(state: RideState): PackageStatus = when (state) {
    RideState.Requested, RideState.Matching, RideState.Offered, RideState.Accepted,
    RideState.DriverArrived,
    -> PackageStatus.PickupPending

    RideState.InProgress -> PackageStatus.InTransit

    else -> PackageStatus.Delivered
}
