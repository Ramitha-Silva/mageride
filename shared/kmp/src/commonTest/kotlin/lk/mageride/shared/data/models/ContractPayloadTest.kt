package lk.mageride.shared.data.models

import lk.mageride.shared.data.models.comms.CallCounterparty
import lk.mageride.shared.data.models.comms.CalleeRole
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.comms.VoipTokenResponse
import lk.mageride.shared.data.models.content.NotificationTemplate
import lk.mageride.shared.data.models.content.OperatingCityListResponse
import lk.mageride.shared.data.models.content.TrilingualText
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.dispatch.PresenceState
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.data.models.iam.VerifyOtpResponse
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.ride.AcceptRideOfferResponse
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.PenaltySettlement
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.safety.SharedTripView
import lk.mageride.shared.data.models.safety.SosEvent
import lk.mageride.shared.data.models.safety.SosRole
import lk.mageride.shared.data.models.subscription.DailyFeeRateList
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketQueue
import lk.mageride.shared.data.models.support.TicketStatus
import lk.mageride.shared.data.models.transit.FeedStatus
import lk.mageride.shared.data.models.transit.FeedUploadStatus
import lk.mageride.shared.data.models.transit.TransitOptionKind
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.SessionEndReason
import lk.mageride.shared.data.models.trip.SessionState
import lk.mageride.shared.data.models.version.AppVersionCheck
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.serialization.MageRideJson
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Instant

/**
 * Response bodies taken from the service contracts in `backend/contracts`, decoded through
 * [MageRideJson].
 *
 * Where [DtoRoundTripTest] proves a DTO agrees with **itself**, this proves it agrees with the
 * **contract**: each payload below is written from the schema in the named file, with its example
 * values where the contract prints any, and the assertions name the fields a wrong reading would
 * silently drop.
 */
class ContractPayloadTest {

    private val vehicleId = "01JQ9F8Z6N5R7T2V4X6Y8A0B2C"
    private val rideId = "01JR9F8Z6N5R7T2V4X6Y8A0B2C"

    private inline fun <reified T> decode(payload: String): T = MageRideJson.decodeFromString<T>(payload)

    // ---- iam.yaml ----------------------------------------------------------------------------

    @Test
    fun verify_otp_flattens_the_token_pair_and_the_user_into_one_object() {
        // iam.yaml POST /v1/auth/otp/verify — allOf(TokenPair, { user, isNewUser }).
        val decoded = decode<VerifyOtpResponse>(
            """
            {"accessToken":"eyJhbGciOiJSUzI1NiJ9.stub","refreshToken":"opaque-rotating","expiresIn":1800,
             "user":{"userId":"$vehicleId","phone":"+94771234567","firstName":"Nimal","role":"driver",
                     "roles":["driver","passenger"],"language":"si","operatingCityCode":"colombo",
                     "defaultPaymentMethod":"cash","notifPrefs":{"SCHEDULED_REMINDER":true},
                     "createdAt":"2026-07-27T04:15:00Z"},
             "isNewUser":true}
            """.trimIndent(),
        )

        assertEquals(1800, decoded.expiresIn)
        assertEquals("opaque-rotating", decoded.tokens.refreshToken)
        assertEquals(Role.DRIVER, decoded.user.role)
        assertEquals(setOf(Role.DRIVER, Role.PASSENGER), decoded.user.effectiveRoles)
        assertEquals(Language.SI, decoded.user.language)
        assertTrue(decoded.isNewUser)
    }

    @Test
    fun a_portal_profile_carries_an_email_and_a_fleet_role() {
        val decoded = decode<UserProfile>(
            """
            {"userId":"$vehicleId","phone":"+94771234567","email":"owner@fleet.lk",
             "role":"fleet_owner","fleetRole":"owner"}
            """.trimIndent(),
        )

        assertEquals(FleetRole.OWNER, decoded.fleetRole)
        assertEquals("owner@fleet.lk", decoded.email)
        assertNull(decoded.language)
    }

    // ---- registry.yaml -----------------------------------------------------------------------

    @Test
    fun a_vehicle_detail_flattens_the_summary_it_composes() {
        // registry.yaml VehicleDetail — allOf(VehicleSummary, { dispatchState, documents, … }).
        val decoded = decode<VehicleDetail>(
            """
            {"vehicleId":"$vehicleId","registrationNumber":"WP-CAB-1234","vehicleType":"three_wheeler",
             "mode":"C","status":"APPROVED","onboardingStatus":"approved","dispatchState":"ACTIVE",
             "driver":{"driverId":"$rideId","name":"Nimal","photoUrl":"https://cdn.mageride.lk/d.jpg"},
             "documents":[{"docId":"$rideId","kind":"revenue_license","status":"EXPIRING",
                           "expiresAt":"2026-09-01T00:00:00Z"}],
             "createdAt":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertEquals(VehicleType.THREE_WHEELER, decoded.vehicleType)
        assertEquals(ServiceMode.C, decoded.mode)
        assertEquals(RegistrationStatus.APPROVED, decoded.status)
        assertEquals(OnboardingStatus.APPROVED, decoded.onboardingStatus)
        assertEquals(DocumentKind.REVENUE_LICENSE, decoded.documents?.single()?.kind)
        assertEquals(DocumentStatus.EXPIRING, decoded.documents?.single()?.status)
    }

    @Test
    fun the_onboarding_read_resumes_at_the_first_step_that_is_not_verified() {
        // registry.yaml GET /v1/vehicles/{vehicleId}/onboarding-status (AL-30).
        val decoded = decode<VehicleOnboardingStatusResponse>(
            """
            {"status":"PENDING","onboardingStatus":"incomplete","nextStep":"revenue",
             "steps":{"details":"VERIFIED","insurance":"VERIFIED","revenue":"PENDING_INPUT",
                      "photos":"PENDING_REVIEW"},
             "fields":[{"key":"licenceNo","value":"B1234567","source":"ai","confidence":0.94,
                        "verifyStatus":"confirmed"},
                       {"key":"nicNo","value":null,"source":"manual","verifyStatus":"pending"}]}
            """.trimIndent(),
        )

        assertEquals(OnboardingStep.REVENUE, decoded.nextStep)
        assertEquals(StepVerdict.PENDING_REVIEW, decoded.steps.photos)
        assertEquals(FieldSource.MANUAL, decoded.fields[1].source)
        assertEquals(VerifyStatus.PENDING, decoded.fields[1].verifyStatus)
        assertNull(decoded.fields[1].value)
    }

    // ---- trip-state.yaml ---------------------------------------------------------------------

    @Test
    fun an_auto_ended_session_states_why_it_ended_and_how_long_it_can_be_restarted() {
        // trip-state.yaml Session — US-5.9/US-5.10.
        val decoded = decode<Session>(
            """
            {"sessionId":"$rideId","vehicleId":"$vehicleId","driverId":"$vehicleId","mode":"A",
             "routeId":"$rideId","state":"AUTO_ENDED","autoEndAtDestination":true,
             "startedAt":"2026-07-27T04:15:00Z","endedAt":"2026-07-27T05:15:00Z",
             "endReason":"destination_geofence","restartableUntil":"2026-07-27T05:20:00Z"}
            """.trimIndent(),
        )

        assertEquals(SessionState.AUTO_ENDED, decoded.state)
        assertEquals(SessionEndReason.DESTINATION_GEOFENCE, decoded.endReason)
        assertTrue(decoded.endReason?.isAutomatic == true)
        assertEquals(Instant.parse("2026-07-27T05:20:00Z"), decoded.restartableUntil)
        assertEquals(ServiceMode.A, decoded.mode)
    }

    // ---- ride.yaml ---------------------------------------------------------------------------

    @Test
    fun a_package_booking_returns_its_pickup_otp_exactly_once() {
        // ride.yaml POST /v1/rides/request — 202 (P-07).
        val decoded = decode<RequestRideResponse>(
            """
            {"rideId":"$rideId","state":"Requested","version":1,"pickupOtp":"4821",
             "estimatedFare":{"amountMinor":45000,"currency":"LKR","surchargeMinor":0}}
            """.trimIndent(),
        )

        assertEquals(RideState.Requested, decoded.state)
        assertEquals(1, decoded.version)
        assertEquals("4821", decoded.pickupOtp)
        assertEquals(Money.ofMinor(45_000), decoded.estimatedFare.money)
    }

    @Test
    fun a_ride_detail_carries_the_counterparty_number_once_the_ride_is_accepted() {
        // ride.yaml RideDetail — AL-48. The passenger sees the driver's number; the driver sees
        // the RIDER's, never the booker's (P-05).
        val decoded = decode<RideDetail>(
            """
            {"rideId":"$rideId","kind":"proxy","state":"Accepted","version":4,
             "bookerId":"$vehicleId","riderId":null,"riderName":"Kamala",
             "pickup":{"lat":6.9271,"lng":79.8612,"address":"Colombo Fort"},
             "dropoff":{"lat":6.9,"lng":79.9},
             "vehicleType":"sedan","paymentMethod":"onepay","offerExpiresAt":"2026-07-27T04:15:15Z",
             "driver":{"driverId":"$vehicleId","name":"Nimal","vehicleType":"sedan",
                       "registrationNumber":"WP-CAB-1234","rating":4.8,"etaSeconds":180},
             "counterpartyPhone":"+94771234567",
             "fare":{"amountMinor":45000,"currency":"LKR"},
             "createdAt":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertEquals(RideKind.PROXY, decoded.kind)
        assertEquals(RideState.Accepted, decoded.state)
        assertTrue(decoded.state.isDriverAssigned)
        assertNull(decoded.riderId, "a proxy rider need not be a registered user (P-01)")
        assertEquals("+94771234567", decoded.counterpartyPhone)
        assertEquals(RidePaymentMethod.ONEPAY, decoded.paymentMethod)
        assertEquals(RideVehicleType.SEDAN, decoded.vehicleType)
    }

    @Test
    fun a_ride_detail_before_acceptance_carries_no_counterparty_number() {
        val decoded = decode<RideDetail>(
            """
            {"rideId":"$rideId","kind":"passenger","state":"Matching","version":2,
             "pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":6.9,"lng":79.9},
             "vehicleType":"motorbike","paymentMethod":"cash","createdAt":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertNull(decoded.counterpartyPhone)
        assertNull(decoded.driver)
        assertTrue(decoded.state.isDriverAssigned.not())
    }

    @Test
    fun the_offer_winner_gets_the_whole_aggregate_back_with_its_new_version() {
        // ride.yaml POST /v1/rides/{rideId}/offer/{driverId}/accept — 200 (R-02, §11.11).
        val decoded = decode<AcceptRideOfferResponse>(
            """
            {"rideId":"$rideId","state":"Accepted","version":5,
             "ride":{"rideId":"$rideId","kind":"passenger","state":"Accepted","version":5,
                     "pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":6.9,"lng":79.9},
                     "vehicleType":"flex","paymentMethod":"lankaqr",
                     "createdAt":"2026-07-27T04:15:00Z"}}
            """.trimIndent(),
        )

        assertEquals(5, decoded.version)
        assertEquals(decoded.version, decoded.ride.version)
        assertEquals(RideVehicleType.FLEX, decoded.ride.vehicleType)
    }

    @Test
    fun a_post_acceptance_cancel_states_the_debt_it_accrued() {
        // ride.yaml POST /v1/rides/{rideId}/cancel — allOf(RideStateChange, { penalty }), D-05.
        val decoded = decode<CancelRideResponse>(
            """
            {"rideId":"$rideId","state":"CancelledByRiderAfterAccept","version":6,
             "penalty":{"amountMinor":5000,"currency":"LKR","settledOn":"next-trip"}}
            """.trimIndent(),
        )

        assertEquals(RideState.CancelledByRiderAfterAccept, decoded.state)
        assertTrue(decoded.state.isTerminal)
        assertEquals(Money.ofMinor(5_000), decoded.penalty?.money)
        assertEquals(PenaltySettlement.NEXT_TRIP, decoded.penalty?.settledOn)
    }

    @Test
    fun a_history_row_carries_enough_driver_detail_to_render_the_call_action() {
        // ride.yaml RideHistoryRow — AL-36; `normal_masked` was removed by AL-48.
        val decoded = decode<RideHistoryRow>(
            """
            {"rideId":"$rideId","state":"Paid","pickup":{"lat":6.9271,"lng":79.8612},
             "dropoff":{"lat":6.9,"lng":79.9},"fare":{"amountMinor":45000,"currency":"LKR"},
             "completedAt":"2026-07-27T05:00:00Z",
             "driver":{"driverId":"$vehicleId","name":"Nimal","mobileMasked":"+9477*****67",
                       "callTypesAvailable":["free_voip","direct_dial"]}}
            """.trimIndent(),
        )

        assertEquals(
            listOf(CallType.FREE_VOIP, CallType.DIRECT_DIAL),
            decoded.driver?.callTypesAvailable,
        )
        assertEquals("+9477*****67", decoded.driver?.mobileMasked)
    }

    @Test
    fun a_declined_location_request_carries_no_coordinates() {
        // ride.yaml LocationRequest — P-02: declining must not leak an approximate position.
        val decoded = decode<LocationRequest>(
            """
            {"requestId":"$rideId","state":"Declined","expiresAt":"2026-07-27T04:20:00Z"}
            """.trimIndent(),
        )

        assertEquals(LocationRequestState.Declined, decoded.state)
        assertNull(decoded.geo)
    }

    // ---- dispatch.yaml -----------------------------------------------------------------------

    @Test
    fun a_job_board_row_is_a_scheduled_ride_with_its_distance_and_intent_count() {
        val decoded = decode<ScheduledRide>(
            """
            {"scheduledRideId":"$rideId","rideId":null,
             "pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":7.2906,"lng":80.6337},
             "vehicleType":"van","pickupTime":"2026-07-28T02:30:00Z","status":"SCHEDULED",
             "distanceM":12400,"intentCount":3}
            """.trimIndent(),
        )

        assertNull(decoded.rideId, "null until dispatch materialises the ride at T-30 min")
        assertEquals(ScheduledRideStatus.SCHEDULED, decoded.status)
        assertEquals(3, decoded.intentCount)
    }

    @Test
    fun the_directional_filter_read_drives_the_countdown_and_the_daily_budget() {
        // dispatch.yaml GET /v1/standby/directional (DT-08, US-6A.18/19).
        val decoded = decode<DirectionalFilterState>(
            """
            {"active":true,"destination":{"lat":6.9271,"lng":79.8612},"label":"Home",
             "expiresAt":"2026-07-27T06:15:00Z","timeRemainingSec":5400,"usesRemaining":1}
            """.trimIndent(),
        )

        assertTrue(decoded.active)
        assertEquals(1, decoded.usesRemaining)
        assertEquals(GeoPoint(lat = 6.9271, lng = 79.8612), decoded.destination)
    }

    @Test
    fun presence_matches_the_driver_presence_check() {
        assertEquals(
            listOf("AVAILABLE", "OFFERED", "OFFLINE", "ON_RIDE"),
            PresenceState.entries.map { it.name }.sorted(),
        )
    }

    // ---- fare.yaml ---------------------------------------------------------------------------

    @Test
    fun a_fare_estimate_binds_its_price_with_a_token_and_shows_only_the_total() {
        // fare.yaml GET /v1/fare/estimate — US-8.9/US-8.4.
        val decoded = decode<FareEstimateResponse>(
            """
            {"fareEstimateToken":"eyJhbGciOiJIUzI1NiJ9.est","amountMinor":45000,"currency":"LKR",
             "breakdown":{"firstKmMinor":10000,"perKmMinor":8000,"distanceKm":5.4,
                          "peakSurchargePct":20,"nightSurchargePct":0}}
            """.trimIndent(),
        )

        assertEquals(Money.ofMinor(45_000), decoded.money)
        assertEquals(5.4, decoded.breakdown.distanceKm)
        assertEquals(20, decoded.breakdown.peakSurchargePct)
    }

    @Test
    fun a_wallet_initiation_carries_the_balance_the_fare_leaves_behind() {
        // fare.yaml POST /v1/fare/pay. Δ AL-57 — `onepay` is gone as a ride method and card
        // acceptance moved one step earlier, to the wallet top-up where MageRide is the payee.
        // Exactly one method block is present.
        val decoded = decode<PaymentInitiation>(
            """
            {"paymentId":"$rideId","state":"Pending","method":"wallet","amountMinor":45000,
             "surchargeMinor":0,"currency":"LKR",
             "wallet":{"balanceAfterMinor":120000}}
            """.trimIndent(),
        )

        assertEquals(PaymentMethod.WALLET, decoded.method)
        assertEquals(PaymentState.Pending, decoded.state)
        // Δ AL-57: the +5% recovered OnePay's ~3% on the ride, and no surviving ride rail touches
        // an acquirer. The field stays in the shape so a client that renders it keeps working.
        assertEquals(0L, decoded.surchargeMinor)
        assertEquals(120_000L, decoded.wallet?.balanceAfterMinor)
        assertNull(decoded.driverQr, "a payment initiation carries exactly one method block")
    }

    @Test
    fun a_driver_qr_initiation_carries_the_drivers_own_bank_qr() {
        // Δ AL-59 — the driver's OWN LankaQR from their verified payout profile, not the
        // platform's merchant QR. There is no callback: the money never passes through MageRide.
        val decoded = decode<PaymentInitiation>(
            """
            {"paymentId":"$rideId","state":"Pending","method":"scan_driver_qr","amountMinor":45000,
             "surchargeMinor":0,"currency":"LKR",
             "driverQr":{"qrImageUrl":"https://cdn.mageride.lk/qr/abc.png"}}
            """.trimIndent(),
        )

        assertEquals(PaymentMethod.SCAN_DRIVER_QR, decoded.method)
        assertEquals("https://cdn.mageride.lk/qr/abc.png", decoded.driverQr?.qrImageUrl)
        assertNull(decoded.wallet)
    }

    @Test
    fun the_driver_qr_pair_moves_the_payment_through_its_two_attestation_states() {
        // fare.yaml /driver-qr/claim → 202, /driver-qr/confirm → 200 (AL-47).
        val claimed = decode<PaymentStatus>(
            """
            {"paymentId":"$rideId","rideId":"$rideId","state":"QrClaimedByPassenger",
             "method":"scan_driver_qr","amountMinor":45000,"currency":"LKR"}
            """.trimIndent(),
        )
        val confirmed = decode<PaymentStatus>(
            """
            {"paymentId":"$rideId","rideId":"$rideId","state":"DriverConfirmedQR",
             "method":"scan_driver_qr","amountMinor":45000,"tipMinor":5000,"currency":"LKR",
             "settledAt":"2026-07-27T05:05:00Z"}
            """.trimIndent(),
        )

        assertEquals(PaymentMethod.SCAN_DRIVER_QR, claimed.method)
        assertTrue(claimed.state.isOffPlatformSettlement)
        assertTrue(claimed.state.isTerminal.not(), "a claim is not yet settled")
        assertTrue(confirmed.state.isTerminal, "DriverConfirmedQR releases the earning (R-05)")
        assertEquals(5_000L, confirmed.tipMinor)
    }

    // ---- subscription.yaml -------------------------------------------------------------------

    @Test
    fun todays_fee_carries_both_the_colombo_date_and_the_instant_it_was_derived_at() {
        // subscription.yaml GET /v1/fees/{driverId}/today — D-13 plus the D-38 tzAt companion.
        val decoded = decode<TodaysDailyFee>(
            """
            {"vehicleType":"three_wheeler","vehicleId":"$vehicleId","dailyRateMinor":10000,"status":"UNPAID",
             "deductedMinor":0,"tripsToday":1,"firstTripFree":true,
             "feeDate":"2026-07-27","feeDateTzAt":"2026-07-26T18:30:00Z"}
            """.trimIndent(),
        )

        assertEquals(VehicleType.THREE_WHEELER, decoded.vehicleType)
        assertTrue(decoded.firstTripFree)
        assertEquals("2026-07-27", decoded.feeDate.toString())
        assertNotNull(decoded.feeDateTzAt)
    }

    @Test
    fun mode_a_is_free_in_the_daily_fee_table() {
        // subscription.yaml GET /v1/fees/rates — bus and train carry no platform fee.
        val decoded = decode<DailyFeeRateList>(
            """
            {"items":[{"vehicleType":"bus","dailyFeeMinor":0,"mode":"A","currency":"LKR"},
                      {"vehicleType":"three_wheeler","dailyFeeMinor":10000,"mode":"C","currency":"LKR"}]}
            """.trimIndent(),
        )

        assertEquals(Money.ZERO, decoded.items.first { it.mode == ServiceMode.A }.money)
        assertEquals(Money.ofMinor(10_000), decoded.items.first { it.mode == ServiceMode.C }.money)
    }

    @Test
    fun a_mode_b_pay_sheet_carries_the_owner_pay_to_block_from_the_verified_profile() {
        // subscription.yaml POST /v1/mode-b/subscriptions/{id}/pay — AL-49.
        val decoded = decode<SubscriptionPayment>(
            """
            {"paymentId":"$rideId","subscriptionId":"$vehicleId","method":"online_transfer",
             "amountMinor":250000,"currency":"LKR","status":"initiated","periodMonth":"2026-08-01",
             "periodMonthTzAt":"2026-07-31T18:30:00Z",
             "payTo":{"bank":"Commercial Bank","branch":"Kollupitiya","accountNo":"1000123456",
                      "accountHolderName":"Sunrise Transport"}}
            """.trimIndent(),
        )

        assertEquals(SubscriptionPayMethod.ONLINE_TRANSFER, decoded.method)
        assertEquals(SubscriptionPaymentStatus.INITIATED, decoded.status)
        assertEquals("2026-08-01", decoded.periodMonth.toString())
        assertEquals("1000123456", decoded.payTo?.accountNo)
        assertEquals(Money.ofMinor(250_000), decoded.money)
    }

    // ---- wallet.yaml -------------------------------------------------------------------------

    @Test
    fun the_wallet_reports_the_balance_net_of_accrued_debt() {
        // wallet.yaml GET /v1/wallet/{userId} — D-05: availableMinor is what the fee gate checks.
        val decoded = decode<Wallet>(
            """
            {"userId":"$vehicleId","balanceMinor":120000,"availableMinor":115000,
             "outstandingDebtMinor":5000,"currency":"LKR","updatedAt":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertEquals(Money.ofMinor(115_000), decoded.money)
        assertEquals(120_000L, decoded.balanceMinor)
        assertEquals(5_000L, decoded.outstandingDebtMinor)
    }

    @Test
    fun a_wallet_debit_is_a_negative_signed_amount() {
        // wallet.yaml WalletTransaction — one of the ledger columns D3' §0 exempts from the
        // non-negative rule, which is why it is not modelled as Money.
        val decoded = decode<WalletTransaction>(
            """
            {"transactionId":"$rideId","entryId":"$vehicleId","kind":"daily_fee",
             "amountMinor":-10000,"currency":"LKR","balanceAfterMinor":110000,
             "reference":"ride:$rideId","occurredAt":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertTrue(decoded.isDebit)
        assertEquals(-10_000L, decoded.amountMinor)
        assertEquals("daily_fee", decoded.kind)
    }

    @Test
    fun topup_states_are_the_three_the_contract_declares() {
        assertEquals(listOf("Failed", "Pending", "Succeeded"), TopupState.entries.map { it.name }.sorted())
    }

    // ---- query.yaml --------------------------------------------------------------------------

    @Test
    fun a_nearby_snapshot_states_when_it_was_taken() {
        // query.yaml GET /v1/nearby — asOf lets a client decide whether a socket frame is newer.
        val decoded = decode<NearbyVehiclesResponse>(
            """
            {"vehicles":[{"vehicleId":"$vehicleId","type":"bus","mode":"A","lat":6.9271,
                          "lng":79.8612,"heading":270,"speed":11.8,"registrationNumber":"NC-1234"}],
             "asOf":"2026-07-27T04:15:00Z"}
            """.trimIndent(),
        )

        assertEquals(VehicleType.BUS, decoded.vehicles.single().type)
        assertEquals(ServiceMode.A, decoded.vehicles.single().mode)
        assertNull(decoded.vehicles.single().driverName, "US-7.12: name only after acceptance")
        assertEquals(Instant.parse("2026-07-27T04:15:00Z"), decoded.asOf)
    }

    @Test
    fun the_earnings_dashboard_nets_the_fee_and_the_penalty_out_of_the_gross() {
        val decoded = decode<EarningsSummary>(
            """
            {"period":"week","rangeFrom":"2026-07-20","rangeTo":"2026-07-26","grossMinor":420000,
             "dailyFeeMinor":70000,"penaltyMinor":5000,"tipMinor":12000,"netMinor":357000,
             "currency":"LKR","trips":34}
            """.trimIndent(),
        )

        assertEquals(Money.ofMinor(357_000), decoded.money)
        assertEquals("2026-07-20", decoded.rangeFrom.toString())
        assertEquals(34, decoded.trips)
    }

    // ---- transit.yaml ------------------------------------------------------------------------

    @Test
    fun a_transit_option_names_the_feed_version_it_was_computed_from() {
        val decoded = decode<TransitOptionsResponse>(
            """
            {"options":[{"kind":"direct","totalDurationSec":2700,"walkingDistanceM":450,
                         "legs":[{"routeId":"R138","routeShortName":"138",
                                  "headsign":"Colombo Fort","boardStopId":"S1","alightStopId":"S9"}]}],
             "feedVersion":"2026-07-01","coverage":"active"}
            """.trimIndent(),
        )

        assertEquals(TransitOptionKind.DIRECT, decoded.options.single().kind)
        assertEquals("138", decoded.options.single().legs.single().routeShortName)
        assertEquals("2026-07-01", decoded.feedVersion)
    }

    @Test
    fun a_gtfs_upload_status_caps_its_error_summary_and_omits_the_tz_companion() {
        // transit.yaml FeedUploadStatus — serviceStart/serviceEnd are read out of the feed rather
        // than derived in Asia/Colombo, so they carry no tzAt (C005 decision 1).
        val decoded = decode<FeedUploadStatus>(
            """
            {"feedVersionId":"$rideId","status":"validated",
             "counts":{"agency":12,"routes":431,"trips":18922,"stops":7644,"stop_times":512303},
             "feedInfoVersion":"2026-07-01","serviceStart":"2026-07-01","serviceEnd":"2026-12-31",
             "warnings":["stable-id changed for 12 stops"],"errorSummary":[]}
            """.trimIndent(),
        )

        assertEquals(FeedStatus.VALIDATED, decoded.status)
        assertTrue(decoded.status.isActivatable)
        assertEquals(512_303L, decoded.counts?.get("stop_times"))
        assertEquals("2026-07-01", decoded.serviceStart.toString())
    }

    // ---- safety.yaml -------------------------------------------------------------------------

    @Test
    fun an_sos_event_records_which_surface_raised_it() {
        val decoded = decode<SosEvent>(
            """
            {"sosId":"$rideId","rideId":"$vehicleId","role":"passenger","lat":6.9271,"lng":79.8612,
             "source":"app","dispatchedAt":"2026-07-27T04:15:02Z"}
            """.trimIndent(),
        )

        assertEquals(SosRole.PASSENGER, decoded.role)
        assertEquals(GeoPoint(lat = 6.9271, lng = 79.8612), decoded.point)
        assertNull(decoded.acknowledgedAt)
    }

    @Test
    fun a_shared_trip_view_is_live_only() {
        // safety.yaml — D-34: position and status as of now, never a replay of the track.
        val decoded = decode<SharedTripView>(
            """
            {"state":"InProgress","position":{"lat":6.9271,"lng":79.8612},"heading":270,
             "vehicle":{"type":"sedan","registrationNumber":"WP-CAB-1234"},"driverName":"Nimal",
             "etaSeconds":420,"asOf":"2026-07-27T04:15:00Z","expiresAt":"2026-07-27T06:15:00Z"}
            """.trimIndent(),
        )

        assertEquals(VehicleType.SEDAN, decoded.vehicle?.type)
        assertEquals(420, decoded.etaSeconds)
    }

    // ---- support.yaml, content.yaml, voip.yaml, version-check.yaml ---------------------------

    @Test
    fun a_ticket_detail_flattens_the_ticket_it_composes() {
        val decoded = decode<TicketDetail>(
            """
            {"ticketId":"$rideId","category":"daily_fee_refund","status":"IN_PROGRESS",
             "queue":"finance","tripId":"$vehicleId","createdAt":"2026-07-27T04:15:00Z",
             "description":"Charged twice on the same day",
             "screenshotUrl":"https://cdn.mageride.lk/s.png","adminResponse":"Reviewing",
             "thread":[{"kind":"opened","at":"2026-07-27T04:15:00Z","toStatus":"OPEN"},
                       {"kind":"responded","at":"2026-07-27T05:00:00Z","fromStatus":"OPEN",
                        "toStatus":"IN_PROGRESS","body":"Reviewing","actorRole":"support_agent"}]}
            """.trimIndent(),
        )

        assertEquals(TicketStatus.IN_PROGRESS, decoded.status)
        assertEquals("daily_fee_refund", decoded.category)
        // Δ C053: a `daily_fee_refund` is Finance's pile, not Support's, and the queue is derived
        // from the category rather than stored — so the two can never disagree.
        assertEquals(TicketQueue.FINANCE, decoded.queue)
        assertEquals(2, decoded.thread.size, "the thread is what makes a resolution visible")
        assertNull(decoded.resolvedAt)
    }

    @Test
    fun an_operating_city_carries_all_three_language_names() {
        // content.yaml GET /v1/config/cities — the §20 seed, Sinhala and Tamil labels intact.
        val decoded = decode<OperatingCityListResponse>(
            """
            {"cities":[{"code":"colombo","nameEn":"Colombo","nameSi":"කොළඹ",
                        "nameTa":"கொழும்பு",
                        "centroid":{"lat":6.9271,"lng":79.8612},"sortOrder":0}]}
            """.trimIndent(),
        )

        val colombo = decoded.cities.single()
        assertEquals("Colombo", colombo.name(Language.EN))
        assertTrue(colombo.name(Language.SI).isNotBlank())
        assertTrue(colombo.name(Language.TA).isNotBlank())
        assertEquals(GeoPoint(lat = 6.9271, lng = 79.8612), colombo.centroid)
    }

    @Test
    fun a_trilingual_template_resolves_per_language() {
        val text = MageRideJson.decodeFromString<TrilingualText>(
            """{"si":"නව","ta":"புதிய","en":"New ride request"}""",
        )

        assertEquals("New ride request", text[Language.EN])
        assertTrue(text[Language.SI].isNotBlank())

        val template = decode<NotificationTemplate>(
            """{"key":"ride_offer","language":"en","version":3,"body":"New ride request: {{pickup}}"}""",
        )
        assertEquals(3, template.version)
        assertEquals(Language.EN, template.language)
    }

    @Test
    fun a_voip_token_names_the_counterparty_and_never_the_booker() {
        // voip.yaml POST /v1/voip/token — P-05.
        val decoded = decode<VoipTokenResponse>(
            """
            {"roomName":"ride_$rideId","token":"lk_jwt","wsUrl":"wss://voip.mageride.lk",
             "callee":"rider"}
            """.trimIndent(),
        )

        assertEquals(CallCounterparty.RIDER, decoded.callee)
        assertEquals("ride_$rideId", decoded.session.roomName)
    }

    @Test
    fun a_direct_dial_call_is_recorded_without_a_session() {
        // voip.yaml POST /v1/calls/start — AL-48: direct_dial creates no session.
        val decoded = decode<StartCallResponse>(
            """{"callId":"$rideId","callType":"direct_dial"}""",
        )

        assertEquals(CallType.DIRECT_DIAL, decoded.callType)
        assertNull(decoded.session)
        assertEquals(CalleeRole.RECIPIENT, MageRideJson.decodeFromString<CalleeRole>("\"recipient\""))
    }

    @Test
    fun the_version_gate_distinguishes_a_blocking_update_from_a_dismissible_one() {
        val decoded = decode<AppVersionCheck>(
            """
            {"updateRequired":true,"latestVersion":"1.6.2",
             "updateUrl":"https://play.google.com/store/apps/details?id=lk.mageride",
             "isMandatory":false}
            """.trimIndent(),
        )

        assertTrue(decoded.updateRequired)
        assertTrue(decoded.isMandatory.not())
    }

    // ---- the cursor envelope over real rows ---------------------------------------------------

    @Test
    fun a_paged_list_response_decodes_as_the_one_generic_envelope() {
        // Every list endpoint is `allOf(CursorPage, { items: [T] })`, so one Page<T> covers all of
        // them and C013 has one pagination helper rather than forty envelopes.
        val history = MageRideJson.decodeFromString(
            Page.serializer(RideHistoryRow.serializer()),
            """
            {"items":[{"rideId":"$rideId","state":"CashSettled",
                       "completedAt":"2026-07-27T05:00:00Z"}],
             "cursor":"b3BhcXVl","hasMore":true}
            """.trimIndent(),
        )
        val transactions = MageRideJson.decodeFromString(
            Page.serializer(WalletTransaction.serializer()),
            """
            {"items":[{"transactionId":"$rideId","entryId":"$vehicleId","kind":"topup",
                       "amountMinor":200000,"currency":"LKR","balanceAfterMinor":320000,
                       "occurredAt":"2026-07-27T04:15:00Z"}],
             "cursor":null,"hasMore":false}
            """.trimIndent(),
        )
        val scheduled = MageRideJson.decodeFromString(
            Page.serializer(ScheduledRide.serializer()),
            """{"items":[],"cursor":null,"hasMore":false}""",
        )

        assertEquals(RideState.CashSettled, history.items.single().state)
        assertEquals("b3BhcXVl", history.cursor)
        assertTrue(transactions.items.single().isDebit.not())
        assertNull(transactions.cursor)
        assertTrue(scheduled.isEmpty)
    }

    // ---- the additive-versioning promise ------------------------------------------------------

    @Test
    fun a_field_added_server_side_does_not_break_an_older_build() {
        // MageRideJson sets ignoreUnknownKeys: the gateway is versioned but additive (D3' §0).
        val decoded = decode<RideStateSnapshotProbe>(
            """{"state":"InProgress","version":7,"surgeReasonAddedLater":"peak"}""",
        )

        assertEquals(RideState.InProgress, decoded.state)
        assertEquals(7, decoded.version)
    }
}

/** Local stand-in so the additive-versioning test does not depend on a service package. */
@kotlinx.serialization.Serializable
private data class RideStateSnapshotProbe(val state: RideState, val version: Int)
