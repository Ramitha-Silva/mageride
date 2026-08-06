package lk.mageride.passenger.ride

import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.fare.DriverQrInitiation
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.CancellationPenalty
import lk.mageride.shared.data.models.ride.PenaltySettlement
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideDriver
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosSmsStatus
import lk.mageride.shared.data.models.safety.TripShareLink
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Duration.Companion.hours

/**
 * The ride seam, in memory.
 *
 * Hand-written for the same reason C079's is: what cluster 4's tests assert is **which call was
 * made and what was in it** — that a decline of a payment never happens, that a cancel carries the
 * version it read, that a claim is followed by a poll. Recorded arguments answer that directly.
 */
internal class FakeRideRepository : RideRepository {

    var rideAnswer: RideDetail = ride()
    var rideFails: Throwable? = null
    var walletBalanceMinor: Long = 100_000
    var paymentState: PaymentState = PaymentState.Initiated

    /** What the poll answers after the claim — set to walk the AL-47 pair. */
    var statusStates: ArrayDeque<PaymentState> = ArrayDeque()

    val cancels = mutableListOf<Triple<Ulid, RideVersion, RideCancelReason>>()
    val payments = mutableListOf<PaymentMethod>()
    val claims = mutableListOf<Pair<Ulid, Ulid?>>()
    val scans = mutableListOf<String>()
    val cashFallbacks = mutableListOf<Ulid>()
    val soses = mutableListOf<Ulid>()
    val calls = mutableListOf<CallType>()

    override suspend fun ride(rideId: Ulid): RideDetail {
        rideFails?.let { throw it }
        return rideAnswer
    }

    override suspend fun rideState(rideId: Ulid): RideStateSnapshot =
        RideStateSnapshot(state = rideAnswer.state, version = rideAnswer.version)

    override suspend fun cancel(rideId: Ulid, version: RideVersion, reason: RideCancelReason): CancelRideResponse {
        cancels += Triple(rideId, version, reason)
        val afterAccept = rideAnswer.state != RideState.Requested &&
            rideAnswer.state != RideState.Matching &&
            rideAnswer.state != RideState.Offered
        return CancelRideResponse(
            rideId = rideId,
            state = if (afterAccept) {
                RideState.CancelledByRiderAfterAccept
            } else {
                RideState.CancelledByRiderBeforeAccept
            },
            version = version + 1,
            penalty = if (afterAccept) {
                CancellationPenalty(
                    amountMinor = PENALTY_MINOR,
                    currency = Currency.LKR,
                    settledOn = PenaltySettlement.NEXT_TRIP,
                )
            } else {
                null
            },
        )
    }

    override suspend fun wallet(userId: Ulid): Wallet =
        Wallet(userId = userId, balanceMinor = walletBalanceMinor, availableMinor = walletBalanceMinor)

    override suspend fun pay(rideId: Ulid, method: PaymentMethod, tipMinor: Long?): PaymentInitiation {
        payments += method
        return PaymentInitiation(
            paymentId = PAYMENT_ID,
            state = if (method == PaymentMethod.WALLET) PaymentState.Succeeded else PaymentState.Initiated,
            method = method,
            amountMinor = FARE_MINOR,
            // Nothing sets a surcharge, because no surviving rail has one (AL-57).
            driverQr = DriverQrInitiation(qrImageUrl = "https://cdn.example/driver-qr.png")
                .takeIf { method == PaymentMethod.SCAN_DRIVER_QR },
        )
    }

    override suspend fun paymentStatus(paymentId: Ulid): PaymentStatus =
        status(statusStates.removeFirstOrNull() ?: paymentState)

    override suspend fun fallbackToCash(paymentId: Ulid): PaymentStatus {
        cashFallbacks += paymentId
        return status(PaymentState.FellBackToCash)
    }

    override suspend fun payByScanningDriverQr(rideId: Ulid, qrPayload: String): PaymentStatus {
        scans += qrPayload
        return status(PaymentState.Initiated)
    }

    override suspend fun claimDriverQrPaid(rideId: Ulid, receiptArtifactId: Ulid?): PaymentStatus {
        claims += rideId to receiptArtifactId
        return status(PaymentState.QrClaimedByPassenger)
    }

    override suspend fun triggerSos(rideId: Ulid, lat: Double, lng: Double): SosDispatched {
        soses += rideId
        return SosDispatched(sosId = SOS_ID, dispatchedAt = Fixtures.NOW, smsStatus = SosSmsStatus.DISPATCHED)
    }

    override suspend fun shareTrip(rideId: Ulid): TripShareLink =
        TripShareLink(token = "tok", url = "https://passenger.mageride.lk/t/tok", expiresAt = Fixtures.NOW + 2.hours)

    override suspend fun startCall(rideId: Ulid, type: CallType): StartCallResponse {
        calls += type
        return StartCallResponse(callId = CALL_ID, callType = type)
    }

    private fun status(state: PaymentState) = PaymentStatus(
        paymentId = PAYMENT_ID,
        rideId = RIDE_ID,
        state = state,
        method = PaymentMethod.SCAN_DRIVER_QR,
        amountMinor = FARE_MINOR,
    )

    internal companion object {
        const val RIDE_ID = "01JRIDE0000000000000000001"
        const val PAYMENT_ID = "01JPAY00000000000000000001"
        const val SOS_ID = "01JSOS00000000000000000001"
        const val CALL_ID = "01JCALL0000000000000000001"
        const val DRIVER_ID = "01JDRV00000000000000000001"

        /** D-05's Rs 50, in minor units. */
        const val PENALTY_MINOR = 5_000L
        const val FARE_MINOR = 85_000L

        /** A ride in whatever state a test needs. */
        fun ride(
            state: RideState = RideState.Matching,
            driver: RideDriver? = null,
            phone: String? = null,
            kind: RideKind = RideKind.PASSENGER,
        ) = RideDetail(
            rideId = RIDE_ID,
            kind = kind,
            state = state,
            version = 3,
            pickup = Place(lat = 6.9344, lng = 79.8428),
            dropoff = Place(lat = 6.8649, lng = 79.8997),
            vehicleType = RideVehicleType.SEDAN,
            paymentMethod = RidePaymentMethod.CASH,
            driver = driver,
            counterpartyPhone = phone,
            fare = lk.mageride.shared.data.models.ride.FareEstimate(amountMinor = FARE_MINOR),
            createdAt = Fixtures.NOW,
        )

        /** An accepted ride, with a driver and — AL-48 — their real number. */
        fun accepted() = ride(
            state = RideState.Accepted,
            driver = RideDriver(driverId = DRIVER_ID, name = "K. Fernando", rating = 4.8, etaSeconds = 180),
            phone = "+94771234567",
        )
    }
}
