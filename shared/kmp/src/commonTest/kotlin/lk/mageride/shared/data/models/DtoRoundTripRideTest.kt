package lk.mageride.shared.data.models

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import lk.mageride.shared.data.models.dispatch.DirectionalConfig
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCleared
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCreated
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.dispatch.DriverLevelAfterNoShow
import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.DriverStatsResponse
import lk.mageride.shared.data.models.dispatch.GoOnlineRequest
import lk.mageride.shared.data.models.dispatch.JobBoardIntentResponse
import lk.mageride.shared.data.models.dispatch.LevelConfig
import lk.mageride.shared.data.models.dispatch.PresenceResponse
import lk.mageride.shared.data.models.dispatch.PresenceState
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.data.models.dispatch.SetDirectionalFilterRequest
import lk.mageride.shared.data.models.fare.CalculateFinalFareRequest
import lk.mageride.shared.data.models.fare.ClaimDriverQrRequest
import lk.mageride.shared.data.models.fare.ConfirmDriverQrRequest
import lk.mageride.shared.data.models.fare.DisputeDriverQrRequest
import lk.mageride.shared.data.models.fare.DriverQrInitiation
import lk.mageride.shared.data.models.fare.FareBreakdown
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.fare.FinalFareResponse
import lk.mageride.shared.data.models.fare.InitiatePaymentRequest
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.fare.ProviderCallback
import lk.mageride.shared.data.models.fare.RefundFareRequest
import lk.mageride.shared.data.models.fare.RefundKind
import lk.mageride.shared.data.models.fare.RefundResponse
import lk.mageride.shared.data.models.fare.RefundStatus
import lk.mageride.shared.data.models.fare.ScanDriverQrRequest
import lk.mageride.shared.data.models.fare.WalletInitiation
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.AcceptRideOfferResponse
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.CancellationPenalty
import lk.mageride.shared.data.models.ride.CompleteRideResponse
import lk.mageride.shared.data.models.ride.ConfirmCashOnDeliveryRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.DeclineRideOfferRequest
import lk.mageride.shared.data.models.ride.DisputeRideRequest
import lk.mageride.shared.data.models.ride.FareEstimate
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.NotifyPaymentSettledRequest
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.data.models.ride.PenaltySettlement
import lk.mageride.shared.data.models.ride.ProofArtifactResponse
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideDriver
import lk.mageride.shared.data.models.ride.RideHistoryDriver
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.RideSagaState
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.ride.RideTransition
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.SystemCancelReason
import lk.mageride.shared.data.models.ride.SystemCancelRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import kotlin.test.Test

/**
 * Round-trips every ride-svc, dispatch-svc and fare-svc DTO — the Mode C hot path.
 *
 * See [assertRoundTrips].
 */
class DtoRoundTripRideTest {

    private val fare = FareEstimate(amountMinor = 45_000, surchargeMinor = 2_250)

    private val rideDetail = RideDetail(
        rideId = Sample.ULID_A,
        kind = RideKind.PACKAGE,
        state = RideState.InProgress,
        version = 7,
        bookerId = Sample.ULID_B,
        riderId = Sample.ULID_C,
        riderName = "Kamala",
        pickup = Sample.PLACE,
        dropoff = Sample.PLACE.copy(address = "Kandy"),
        vehicleType = RideVehicleType.MINI_TRUCK,
        paymentMethod = RidePaymentMethod.COD,
        scheduledAt = Sample.LATER,
        offerExpiresAt = Sample.LATER,
        driver = RideDriver(
            driverId = Sample.ULID_B,
            name = "Nimal",
            photoUrl = Sample.URL,
            vehicleType = VehicleType.MINI_TRUCK,
            registrationNumber = "WP-LORRY-9",
            rating = 4.8,
            etaSeconds = 180,
        ),
        counterpartyPhone = Sample.PHONE,
        fare = fare,
        packageSize = PackageSize.L,
        packageDescription = "Two cartons",
        packageStatus = PackageStatus.InTransit,
        createdAt = Sample.AT,
    )

    // ---- ride.yaml ---------------------------------------------------------------------------

    @Test
    fun the_booking_dtos_round_trip() {
        assertRoundTrips(
            RideRequest(
                clientRequestId = Sample.ULID_A,
                kind = RideKind.PROXY,
                pickup = Sample.PLACE,
                dropoff = Sample.PLACE.copy(address = "Kandy"),
                vehicleType = RideVehicleType.FLEX,
                fareEstimateToken = "est-token",
                paymentMethod = RidePaymentMethod.ONEPAY,
                scheduledAt = Sample.LATER,
                isProxy = true,
                riderName = "Kamala",
                riderPhone = Sample.PHONE,
                packageSize = PackageSize.M,
                packageDescription = "Documents",
                recipientName = "Sunil",
                recipientPhone = Sample.PHONE,
            ),
        )
        assertRoundTrips(fare)
        assertRoundTrips(
            RequestRideResponse(
                rideId = Sample.ULID_A,
                state = RideState.Requested,
                version = 1,
                pickupOtp = "4821",
                estimatedFare = fare,
            ),
        )
        assertRoundTrips(rideDetail)
    }

    @Test
    fun the_ride_transition_dtos_round_trip() {
        assertRoundTrips(VersionedCommand(version = 3))
        assertRoundTrips(OtpAttempt("4821"))
        assertRoundTrips(RideStateChange(Sample.ULID_A, RideState.DriverArrived, 4))
        assertRoundTrips(RideStateSnapshot(RideState.Offered, 2, Sample.LATER))
        assertRoundTrips(AcceptRideOfferRequest(Sample.ULID_B, version = 2))
        assertRoundTrips(
            AcceptRideOfferResponse(Sample.ULID_A, RideState.Accepted, 3, rideDetail),
        )
        assertRoundTrips(DeclineRideOfferRequest(Sample.ULID_B))
        assertRoundTrips(StartRideRequest(version = 4, otp = "4821"))
        assertRoundTrips(
            CompleteRideResponse(Sample.ULID_A, RideState.PaymentPending, 6, fare),
        )
    }

    @Test
    fun the_cancellation_dtos_round_trip() {
        assertRoundTrips(CancelRideRequest(version = 5, reason = RideCancelReason.DRIVER_TOO_FAR))
        val penalty = CancellationPenalty(
            amountMinor = 5_000,
            currency = Currency.LKR,
            settledOn = PenaltySettlement.NEXT_TRIP,
        )
        assertRoundTrips(penalty)
        assertRoundTrips(
            CancelRideResponse(
                rideId = Sample.ULID_A,
                state = RideState.CancelledByRiderAfterAccept,
                version = 6,
                penalty = penalty,
            ),
        )
        assertRoundTrips(DisputeRideRequest("Charged twice for the same trip"))
        assertRoundTrips(SystemCancelRideRequest(SystemCancelReason.DRIVER_OFFLINE_GRACE_EXPIRED))
    }

    @Test
    fun the_package_and_history_dtos_round_trip() {
        assertRoundTrips(ConfirmCashOnDeliveryRequest(collectedMinor = 45_000))
        assertRoundTrips(ProofArtifactResponse(Sample.ULID_A))
        val historyDriver = RideHistoryDriver(
            driverId = Sample.ULID_B,
            name = "Nimal",
            mobileMasked = Sample.PHONE_MASKED,
            callTypesAvailable = listOf(CallType.FREE_VOIP, CallType.DIRECT_DIAL),
        )
        assertRoundTrips(historyDriver)
        assertRoundTrips(
            RideHistoryRow(
                rideId = Sample.ULID_A,
                state = RideState.Paid,
                pickup = Sample.PLACE,
                dropoff = Sample.PLACE,
                fare = fare,
                completedAt = Sample.LATER,
                driver = historyDriver,
            ),
        )
    }

    @Test
    fun the_location_request_and_saga_dtos_round_trip() {
        assertRoundTrips(CreateLocationRequestRequest(Sample.PHONE, Sample.ULID_A))
        assertRoundTrips(
            CreateLocationRequestResponse(
                requestId = Sample.ULID_A,
                state = LocationRequestState.RiderNotRegistered,
                ttl = CreateLocationRequestResponse.TTL_SECONDS,
            ),
        )
        assertRoundTrips(
            LocationRequest(
                requestId = Sample.ULID_A,
                state = LocationRequestState.Confirmed,
                geo = Sample.POINT_WITH_ACCURACY,
                expiresAt = Sample.LATER,
            ),
        )
        assertRoundTrips(
            NotifyPaymentSettledRequest(Sample.ULID_A, PaymentState.FellBackToCash, 45_000),
        )
        val transition = RideTransition(
            from = RideState.Accepted,
            to = RideState.DriverArrived,
            at = Sample.AT,
            actor = "driver:${Sample.ULID_B}",
        )
        assertRoundTrips(transition)
        assertRoundTrips(
            RideSagaState(
                rideId = Sample.ULID_A,
                state = RideState.PaymentPending,
                version = 6,
                transitions = listOf(transition),
                pendingOutbox = 2,
            ),
        )
    }

    // ---- dispatch.yaml -----------------------------------------------------------------------

    @Test
    fun the_standby_and_directional_dtos_round_trip() {
        assertRoundTrips(GoOnlineRequest(Sample.ULID_A, Sample.POINT, Sample.POINT))
        assertRoundTrips(PresenceResponse(PresenceState.AVAILABLE))
        assertRoundTrips(SetDirectionalFilterRequest(Sample.POINT, "Home"))
        assertRoundTrips(
            DirectionalFilterCreated(
                filterId = Sample.ULID_A,
                expiresAt = Sample.LATER,
                usesRemaining = 1,
                maxDurationSec = 7_200,
            ),
        )
        assertRoundTrips(
            DirectionalFilterState(
                active = true,
                destination = Sample.POINT,
                label = "Home",
                expiresAt = Sample.LATER,
                timeRemainingSec = 5_400,
                usesRemaining = 1,
            ),
        )
        assertRoundTrips(DirectionalFilterCleared(active = false, usesRemaining = 1))
        assertRoundTrips(
            DirectionalConfig(
                thetaMaxDeg = 45,
                detourMaxM = 2_000,
                progressMinM = 250,
                maxUsesPerDay = 2,
                maxDurationSec = 7_200,
                clearOnFirstTrip = false,
            ),
        )
    }

    @Test
    fun the_job_board_and_level_dtos_round_trip() {
        assertRoundTrips(
            ScheduleRideRequest(
                pickupLat = 6.9271,
                pickupLng = 79.8612,
                destLat = 7.2906,
                destLng = 80.6337,
                pickupTime = Sample.LATER,
                vehicleType = RideVehicleType.VAN,
            ),
        )
        assertRoundTrips(
            ScheduledRide(
                scheduledRideId = Sample.ULID_A,
                rideId = Sample.ULID_B,
                pickup = Sample.PLACE,
                dropoff = Sample.PLACE,
                vehicleType = RideVehicleType.VAN,
                pickupTime = Sample.LATER,
                status = ScheduledRideStatus.DISPATCHED,
                distanceM = 12_400,
                intentCount = 3,
            ),
        )
        assertRoundTrips(JobBoardIntentResponse(Sample.ULID_A, Sample.ULID_B))
        assertRoundTrips(DriverLevelResponse(level = 3, ratingPoints = 420, levelUpThreshold = 500))
        assertRoundTrips(DriverStatsResponse(acceptanceRate = 0.92, noShows = 1, points = 420))
        assertRoundTrips(DriverLevelAfterNoShow(Sample.ULID_A, level = 2))
        assertRoundTrips(
            LevelConfig(
                levelUpThreshold = 500,
                noShowPenaltyPoints = 50,
                cancellationPenaltyPoints = 25,
                jobBoardMinLevel = 2,
            ),
        )
    }

    // ---- fare.yaml ---------------------------------------------------------------------------

    private val breakdown = FareBreakdown(
        firstKmMinor = 10_000,
        perKmMinor = 8_000,
        distanceKm = 5.4,
        peakSurchargePct = 20,
        nightSurchargePct = 15,
    )

    @Test
    fun the_fare_calculation_dtos_round_trip() {
        assertRoundTrips(breakdown)
        assertRoundTrips(
            FareEstimateResponse(
                fareEstimateToken = "est-token",
                amountMinor = 45_000,
                currency = Currency.LKR,
                breakdown = breakdown,
            ),
        )
        assertRoundTrips(CalculateFinalFareRequest(Sample.ULID_A, distanceKm = 5.4, durationSec = 900))
        assertRoundTrips(
            FinalFareResponse(
                paymentId = Sample.ULID_A,
                amountMinor = 45_000,
                currency = Currency.LKR,
                breakdown = breakdown,
            ),
        )
    }

    @Test
    fun the_payment_dtos_round_trip() {
        assertRoundTrips(
            InitiatePaymentRequest(Sample.ULID_A, PaymentMethod.ONEPAY, tipMinor = 5_000),
        )
        assertRoundTrips(WalletInitiation(balanceAfterMinor = 120_000))
        assertRoundTrips(DriverQrInitiation(Sample.URL))
        assertRoundTrips(
            PaymentInitiation(
                paymentId = Sample.ULID_A,
                state = PaymentState.Pending,
                method = PaymentMethod.SCAN_DRIVER_QR,
                amountMinor = 45_000,
                surchargeMinor = 0,
                currency = Currency.LKR,
                wallet = WalletInitiation(balanceAfterMinor = 120_000),
                driverQr = DriverQrInitiation(Sample.URL),
            ),
        )
        assertRoundTrips(
            PaymentStatus(
                paymentId = Sample.ULID_A,
                rideId = Sample.ULID_B,
                state = PaymentState.PartiallyRefunded,
                method = PaymentMethod.CASH,
                amountMinor = 45_000,
                surchargeMinor = 0,
                tipMinor = 5_000,
                currency = Currency.LKR,
                settledAt = Sample.LATER,
            ),
        )
    }

    @Test
    fun the_driver_qr_and_refund_dtos_round_trip() {
        assertRoundTrips(ScanDriverQrRequest(Sample.ULID_A, "00020101021230"))
        assertRoundTrips(ClaimDriverQrRequest(Sample.ULID_A, Sample.ULID_B))
        assertRoundTrips(ConfirmDriverQrRequest(Sample.ULID_A))
        assertRoundTrips(DisputeDriverQrRequest(Sample.ULID_A, "Money never arrived"))
        assertRoundTrips(
            ProviderCallback(
                providerTransactionId = "OP-778812",
                paymentId = Sample.ULID_A,
                status = ProviderCallbackStatus.SUCCESS,
                amountMinor = 45_000,
                currency = Currency.LKR,
                raw = JsonObject(mapOf("gatewayRef" to JsonPrimitive("OP-778812"))),
            ),
        )
        assertRoundTrips(
            RefundFareRequest(
                paymentId = Sample.ULID_A,
                kind = RefundKind.OVERPAID_REVERSAL,
                amountMinor = 45_000,
                currency = Currency.LKR,
                reasonCode = "late_callback",
            ),
        )
        assertRoundTrips(
            RefundResponse(Sample.ULID_A, RefundStatus.Submitted, 45_000, Currency.LKR),
        )
    }
}
