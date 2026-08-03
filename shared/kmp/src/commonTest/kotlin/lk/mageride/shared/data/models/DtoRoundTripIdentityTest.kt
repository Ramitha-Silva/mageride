package lk.mageride.shared.data.models

import lk.mageride.shared.data.models.iam.ActiveTrip
import lk.mageride.shared.data.models.iam.ActiveTripKind
import lk.mageride.shared.data.models.iam.ActiveTripRole
import lk.mageride.shared.data.models.iam.AppConfig
import lk.mageride.shared.data.models.iam.AuthSessionResponse
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethodPreference
import lk.mageride.shared.data.models.iam.DeleteAccountResponse
import lk.mageride.shared.data.models.iam.DriverShift
import lk.mageride.shared.data.models.iam.EffectivePermissions
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactInput
import lk.mageride.shared.data.models.iam.EmergencyContactListResponse
import lk.mageride.shared.data.models.iam.GoogleAuthCodeLogin
import lk.mageride.shared.data.models.iam.IdTokenLogin
import lk.mageride.shared.data.models.iam.IssueMqttTokenRequest
import lk.mageride.shared.data.models.iam.IssueMqttTokenResponse
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.LoginBootstrap
import lk.mageride.shared.data.models.iam.LookupUserResponse
import lk.mageride.shared.data.models.iam.PasswordLogin
import lk.mageride.shared.data.models.iam.PermissionEntry
import lk.mageride.shared.data.models.iam.PermissionGrant
import lk.mageride.shared.data.models.iam.RefreshSessionRequest
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.iam.RequestOtpResponse
import lk.mageride.shared.data.models.iam.ResendOtpRequest
import lk.mageride.shared.data.models.iam.ResendOtpResponse
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressInput
import lk.mageride.shared.data.models.iam.SavedAddressListResponse
import lk.mageride.shared.data.models.iam.TokenPair
import lk.mageride.shared.data.models.iam.UpdateProfileRequest
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.data.models.iam.VerifyOtpRequest
import lk.mageride.shared.data.models.iam.VerifyOtpResponse
import lk.mageride.shared.data.models.registry.AcceptShareGrantResponse
import lk.mageride.shared.data.models.registry.BindVehicleDeviceRequest
import lk.mageride.shared.data.models.registry.BindVehicleDeviceResponse
import lk.mageride.shared.data.models.registry.CreateShareGrantRequest
import lk.mageride.shared.data.models.registry.CreateShareGrantResponse
import lk.mageride.shared.data.models.registry.DispatchState
import lk.mageride.shared.data.models.registry.DriverPayoutProfile
import lk.mageride.shared.data.models.registry.GrantStatus
import lk.mageride.shared.data.models.registry.ModeBBilling
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.OnboardingStepInput
import lk.mageride.shared.data.models.registry.OnboardingStepVerdicts
import lk.mageride.shared.data.models.registry.PayoutDocumentKind
import lk.mageride.shared.data.models.registry.PayoutProfileStatus
import lk.mageride.shared.data.models.registry.RegisterVehicleResponse
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.RequestVehicleAccessRequest
import lk.mageride.shared.data.models.registry.RequestVehicleAccessResponse
import lk.mageride.shared.data.models.registry.SaveOnboardingStepResponse
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.UpdateVehicleDriverProfileRequest
import lk.mageride.shared.data.models.registry.UploadedPayoutDocument
import lk.mageride.shared.data.models.registry.UpsertDriverPayoutProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileResponse
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleDocument
import lk.mageride.shared.data.models.registry.VehicleDriverProfile
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.registry.VehicleRegistration
import lk.mageride.shared.data.models.registry.VehicleStatusResponse
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.data.models.registry.VehicleVerificationVerdicts
import lk.mageride.shared.data.models.trip.AutoEndReason
import lk.mageride.shared.data.models.trip.AutoEndSessionRequest
import lk.mageride.shared.data.models.trip.DriverRatingInput
import lk.mageride.shared.data.models.trip.Rating
import lk.mageride.shared.data.models.trip.RatingInput
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.SessionEndReason
import lk.mageride.shared.data.models.trip.SessionState
import lk.mageride.shared.data.models.trip.StartSessionRequest
import kotlin.test.Test

/**
 * Round-trips every core, iam, registry and trip-state DTO with **every** property populated.
 *
 * See [assertRoundTrips]. Splitting the sweep across four files keeps each one readable; between
 * them they cover every `@Serializable` type this component publishes.
 */
class DtoRoundTripIdentityTest {

    // ---- core (_shared.yaml) -----------------------------------------------------------------

    @Test
    fun the_shared_primitives_round_trip() {
        assertRoundTrips(Money(amountMinor = 48_000, currency = Currency.LKR))
        assertRoundTrips(Sample.POINT)
        assertRoundTrips(Sample.POINT_WITH_ACCURACY)
        assertRoundTrips(Sample.PLACE)
        assertRoundTrips(Sample.EXTRACTED_FIELD)
        assertRoundTrips(CallbackAck(received = true))
        assertRoundTrips(
            ProblemDetails(
                type = ErrorCode.OFFER_EXPIRED.typeUri,
                title = "Offer has expired",
                status = 410,
                detail = "The 15-second offer window closed",
                instance = "/v1/rides/${Sample.ULID_A}/offer/${Sample.ULID_B}/accept",
                traceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                errors = mapOf("offerId" to listOf("expired")),
                updateUrl = Sample.URL,
                latestVersion = "1.6.2",
                isMandatory = false,
            ),
        )
    }

    @Test
    fun a_position_sample_round_trips_with_every_optional_column_populated() {
        assertRoundTrips(
            PositionSample(
                vehicleId = Sample.ULID_A,
                sampleTs = Sample.AT,
                receivedTs = Sample.LATER,
                seq = 84_213,
                lat = 6.9271,
                lng = 79.8612,
                speedMps = 11.8,
                headingDeg = 270,
                accuracyM = 7.5,
                hdop = 0.9,
                satCount = 11,
                source = PositionSource.NMEA_MQTT,
                mode = ServiceMode.B,
                vehicleType = VehicleType.VAN,
                fleetId = Sample.ULID_B,
                tripId = Sample.ULID_C,
            ),
        )
    }

    // ---- iam.yaml ----------------------------------------------------------------------------

    private val profile = UserProfile(
        userId = Sample.ULID_A,
        phone = Sample.PHONE,
        email = "owner@fleet.lk",
        firstName = "Nimal",
        photoUrl = Sample.URL,
        role = Role.DRIVER,
        roles = listOf(Role.DRIVER, Role.PASSENGER),
        fleetRole = FleetRole.MANAGER,
        language = Language.TA,
        operatingCityCode = "colombo",
        defaultPaymentMethod = DefaultPaymentMethod.LANKAQR,
        notifPrefs = mapOf("SCHEDULED_REMINDER" to true, "LOW_BALANCE" to false),
        createdAt = Sample.AT,
    )

    @Test
    fun the_emergency_contact_and_payment_preference_dtos_round_trip() {
        // Δ MCS-03 — D-33's SOS recipients (AL-13) and AL-14's payment default.
        val contact = EmergencyContact(Sample.ULID_A, isPrimary = true, name = "Amara", phone = Sample.PHONE)
        assertRoundTrips(EmergencyContactInput("Amara", Sample.PHONE))
        assertRoundTrips(contact)
        assertRoundTrips(EmergencyContactListResponse(listOf(contact)))
        assertRoundTrips(DefaultPaymentMethodPreference(DefaultPaymentMethod.CASH))
    }

    /** A Mode C ride in flight — the state a device switch has to restore (US-1.14). */
    private val activeTrip = ActiveTrip(
        tripId = Sample.ULID_C,
        kind = ActiveTripKind.RIDE,
        role = ActiveTripRole.PASSENGER,
        state = "Accepted",
        mode = ServiceMode.C,
        vehicleId = Sample.ULID_A,
        counterpartyId = Sample.ULID_B,
        pickup = Sample.POINT,
        dropoff = Sample.POINT,
        startedAt = Sample.AT,
    )

    /** Today's shift, from the `fares.driver_earnings` rollup for the Colombo day (D-38). */
    private val driverShift = DriverShift(
        isOnline = true,
        activeSessionId = Sample.ULID_A,
        activeVehicleId = Sample.ULID_B,
        businessDate = Sample.DAY,
        todayTrips = 4,
        todayGross = Money(amountMinor = 128_000, currency = Currency.LKR),
        todayDailyFee = Money(amountMinor = 10_000, currency = Currency.LKR),
    )

    @Test
    fun the_bootstrap_and_rbac_dtos_round_trip() {
        // Δ MCS-03 — AL-14/US-1.15's eager-fetch payload and AL-06's RBAC matrix.
        val saved = SavedAddress(
            addressId = Sample.ULID_A,
            label = "Home",
            line1 = "42 Galle Road",
            line2 = "Bambalapitiya",
            line3 = "Colombo 04",
            lat = 6.8905,
            lng = 79.8565,
            isHome = true,
            isWork = false,
        )

        val contact = EmergencyContact(Sample.ULID_A, isPrimary = true, name = "Amara", phone = Sample.PHONE)

        val permissions = EffectivePermissions(
            userId = Sample.ULID_A,
            roles = listOf(Role.DRIVER, Role.PASSENGER),
            fleetRole = FleetRole.MANAGER,
            fleetId = Sample.ULID_B,
            permissions = listOf(
                PermissionEntry(
                    featureArea = "end-user-account-management",
                    label = "End-user account management",
                    grants = listOf(PermissionGrant.READ, PermissionGrant.WRITE),
                    scopedGrants = listOf(PermissionGrant.OWN_SCOPE),
                    symbol = "◐ on tickets",
                    qualifier = "own org",
                ),
            ),
        )
        assertRoundTrips(permissions)
        assertRoundTrips(
            LoginBootstrap(
                profile = profile,
                savedAddresses = listOf(saved),
                emergencyContacts = listOf(contact),
                defaultPaymentMethod = DefaultPaymentMethod.CASH,
                paymentMethods = listOf(DefaultPaymentMethod.CASH),
                activeTrip = activeTrip,
                driver = driverShift,
                config = AppConfig(cities = emptyList(), featureFlags = mapOf("directional" to true)),
                permissions = permissions,
            ),
        )
    }

    @Test
    fun the_auth_dtos_round_trip() {
        assertRoundTrips(profile)
        assertRoundTrips(TokenPair("access", "refresh", TokenPair.ACCESS_TOKEN_LIFETIME_SECONDS))
        assertRoundTrips(
            RequestOtpRequest(
                phone = Sample.PHONE,
                deviceId = "device-1",
                fcmToken = "fcm-1",
                role = AppSurface.DRIVER,
            ),
        )
        assertRoundTrips(
            RequestOtpResponse(
                authId = Sample.ULID_A,
                attemptsRemaining = 4,
                cooldownSeconds = 60,
                isBlocked = false,
            ),
        )
        assertRoundTrips(VerifyOtpRequest(Sample.ULID_A, "482193", "device-1"))
        assertRoundTrips(ResendOtpRequest(Sample.ULID_A))
        assertRoundTrips(ResendOtpResponse(attemptsRemaining = 3, cooldownSeconds = 60))
        assertRoundTrips(RefreshSessionRequest("refresh"))
        assertRoundTrips(PasswordLogin("owner@fleet.lk", "correct-horse-battery"))
        assertRoundTrips(IdTokenLogin("id-token"))
        assertRoundTrips(GoogleAuthCodeLogin("auth-code", "https://admin.mageride.lk/callback"))
    }

    @Test
    fun the_session_responses_round_trip() {
        assertRoundTrips(
            VerifyOtpResponse(
                accessToken = "access",
                refreshToken = "refresh",
                expiresIn = TokenPair.ACCESS_TOKEN_LIFETIME_SECONDS,
                user = profile,
                isNewUser = true,
            ),
        )
        assertRoundTrips(
            AuthSessionResponse(
                accessToken = "access",
                refreshToken = "refresh",
                expiresIn = TokenPair.ACCESS_TOKEN_LIFETIME_SECONDS,
                user = profile,
            ),
        )
        assertRoundTrips(IssueMqttTokenRequest(Sample.ULID_A, "device-1", Sample.ULID_B))
        assertRoundTrips(
            IssueMqttTokenResponse("mqtt-jwt", IssueMqttTokenResponse.MIN_LIFETIME_SECONDS),
        )
    }

    @Test
    fun the_profile_and_saved_address_dtos_round_trip() {
        assertRoundTrips(
            UpdateProfileRequest(
                firstName = "Nimal",
                photoUrl = Sample.URL,
                language = Language.SI,
                notifPrefs = mapOf("LOW_BALANCE" to true),
            ),
        )
        assertRoundTrips(DeleteAccountResponse(Sample.ULID_A))
        assertRoundTrips(LookupUserResponse(registered = true, userId = Sample.ULID_A))
        assertRoundTrips(LanguagePreference(Language.TA))

        val input = SavedAddressInput(
            label = "Home",
            line1 = "12 Galle Road",
            line2 = "Kollupitiya",
            line3 = "Colombo 03",
            lat = 6.9271,
            lng = 79.8612,
            isHome = true,
            isWork = false,
        )
        assertRoundTrips(input)
        val saved = SavedAddress(
            addressId = Sample.ULID_A,
            label = input.label,
            line1 = input.line1,
            line2 = input.line2,
            line3 = input.line3,
            lat = input.lat,
            lng = input.lng,
            isHome = input.isHome,
            isWork = input.isWork,
        )
        assertRoundTrips(saved)
        assertRoundTrips(SavedAddressListResponse(listOf(saved)))
    }

    // ---- registry.yaml -----------------------------------------------------------------------

    private val vehicleSummary = VehicleSummary(
        vehicleId = Sample.ULID_A,
        registrationNumber = "WP-CAB-1234",
        vehicleType = VehicleType.THREE_WHEELER,
        mode = ServiceMode.C,
        status = RegistrationStatus.APPROVED,
        onboardingStatus = OnboardingStatus.APPROVED,
        modeBBilling = ModeBBilling.PAID,
        defaultMonthlyFareMinor = 250_000,
    )

    @Test
    fun the_driver_profile_and_vehicle_registration_dtos_round_trip() {
        assertRoundTrips(
            UpsertDriverProfileRequest(
                driverName = "Nimal Perera",
                profilePhotoFileId = Sample.ULID_A,
                licenseFrontFileId = Sample.ULID_B,
                licenseBackFileId = Sample.ULID_C,
                nicNo = "199012345678",
                allowedVehicleTypes = listOf(VehicleType.THREE_WHEELER, VehicleType.SEDAN),
            ),
        )
        assertRoundTrips(
            UpsertDriverProfileResponse(
                driverId = Sample.ULID_A,
                status = RegistrationStatus.PENDING,
                displayName = "Nimal Perera",
                photoUrl = Sample.URL,
                nicNo = "199012345678",
                allowedVehicleTypes = listOf(VehicleType.THREE_WHEELER, VehicleType.SEDAN),
                fields = listOf(Sample.EXTRACTED_FIELD),
            ),
        )
        assertRoundTrips(
            VehicleRegistration(
                registrationNumber = "WP-CAB-1234",
                vehicleType = RideVehicleType.THREE_WHEELER,
                mode = ServiceMode.C,
                insuranceFileId = Sample.ULID_A,
                revenueLicenseFileId = Sample.ULID_B,
                vehiclePhotoFrontFileId = Sample.ULID_C,
                vehiclePhotoBackFileId = Sample.ULID_A,
                driverName = "Nimal Perera",
                driverPhotoFileId = Sample.ULID_B,
            ),
        )

        // Δ MCS-03 — AL-58/AL-59's payout profile: what a payout is actually sent to.
        assertRoundTrips(UpsertDriverPayoutProfileRequest("BOC", "Bambalapitiya", "8012345678", "K. Fernando"))
        assertRoundTrips(
            DriverPayoutProfile(
                bank = "BOC",
                branch = "Bambalapitiya",
                accountNo = "8012345678",
                accountHolderName = "K. Fernando",
                proofDocId = Sample.ULID_A,
                lankaqrDocId = Sample.ULID_B,
                status = PayoutProfileStatus.PENDING_VERIFICATION,
                rejectionReason = "Statement unreadable",
                verifiedAt = Sample.LATER,
            ),
        )
        assertRoundTrips(UploadedPayoutDocument(Sample.ULID_C, PayoutDocumentKind.LANKAQR_CODE))
    }

    @Test
    fun the_vehicle_read_dtos_round_trip() {
        val verdicts = VehicleVerificationVerdicts(
            vehicleDetails = StepVerdict.VERIFIED,
            insurance = StepVerdict.VERIFIED,
            revenueLicense = StepVerdict.PENDING_REVIEW,
            photos = StepVerdict.PENDING_INPUT,
        )
        assertRoundTrips(verdicts)
        assertRoundTrips(
            RegisterVehicleResponse(
                vehicleId = Sample.ULID_A,
                status = RegistrationStatus.PENDING,
                ocrJobId = Sample.ULID_B,
                registrationNumber = "WP-CAB-1234",
                verification = verdicts,
                onboardingStatus = OnboardingStatus.INCOMPLETE,
                nextStep = OnboardingStep.INSURANCE,
                createdAt = Sample.AT,
            ),
        )
        assertRoundTrips(vehicleSummary)
        assertRoundTrips(VehicleListResponse(listOf(vehicleSummary)))
        assertRoundTrips(
            VehicleStatusResponse(RegistrationStatus.REJECTED, "Plate did not match the photo"),
        )
        assertRoundTrips(VehicleDriverProfile(Sample.ULID_A, "Nimal", Sample.URL))
        assertRoundTrips(
            VehicleDocument(Sample.ULID_A, DocumentKind.INSURANCE, DocumentStatus.VALID, Sample.LATER),
        )
    }

    @Test
    fun a_vehicle_detail_round_trips_with_every_composed_field() {
        assertRoundTrips(
            VehicleDetail(
                vehicleId = vehicleSummary.vehicleId,
                registrationNumber = vehicleSummary.registrationNumber,
                vehicleType = vehicleSummary.vehicleType,
                mode = vehicleSummary.mode,
                status = vehicleSummary.status,
                onboardingStatus = vehicleSummary.onboardingStatus,
                modeBBilling = vehicleSummary.modeBBilling,
                defaultMonthlyFareMinor = vehicleSummary.defaultMonthlyFareMinor,
                dispatchState = DispatchState.DISPATCH_SUSPENDED,
                rejectionReason = "Revenue licence expired",
                fleetId = Sample.ULID_B,
                driver = VehicleDriverProfile(Sample.ULID_A, "Nimal", Sample.URL),
                documents = listOf(
                    VehicleDocument(
                        docId = Sample.ULID_C,
                        kind = DocumentKind.REVENUE_LICENSE,
                        status = DocumentStatus.EXPIRED,
                        expiresAt = Sample.AT,
                    ),
                ),
                createdAt = Sample.AT,
            ),
        )
    }

    @Test
    fun the_onboarding_step_dtos_round_trip() {
        val steps = OnboardingStepVerdicts(
            details = StepVerdict.VERIFIED,
            insurance = StepVerdict.VERIFIED,
            revenue = StepVerdict.PENDING_INPUT,
            photos = StepVerdict.PENDING_REVIEW,
        )
        assertRoundTrips(steps)
        assertRoundTrips(
            VehicleOnboardingStatusResponse(
                status = RegistrationStatus.PENDING,
                onboardingStatus = OnboardingStatus.INCOMPLETE,
                nextStep = OnboardingStep.REVENUE,
                steps = steps,
                fields = listOf(Sample.EXTRACTED_FIELD),
            ),
        )
        assertRoundTrips(
            OnboardingStepInput(
                registrationNumber = "WP-CAB-1234",
                vehicleType = RideVehicleType.SEDAN,
                fileId = Sample.ULID_A,
                fileIdBack = Sample.ULID_B,
                fields = mapOf("nicNo" to "199012345678"),
            ),
        )
        assertRoundTrips(
            SaveOnboardingStepResponse(
                stepStatus = StepVerdict.PENDING_REVIEW,
                onboardingStatus = OnboardingStatus.INCOMPLETE,
                status = RegistrationStatus.PENDING,
                nextStep = OnboardingStep.PHOTOS,
                ocrJobId = Sample.ULID_C,
            ),
        )
        assertRoundTrips(UpdateVehicleDriverProfileRequest("Nimal", Sample.URL))
    }

    @Test
    fun the_device_binding_and_sharing_dtos_round_trip() {
        assertRoundTrips(BindVehicleDeviceRequest("352093081452312"))
        assertRoundTrips(BindVehicleDeviceResponse(Sample.ULID_A))
        assertRoundTrips(CreateShareGrantRequest(Sample.ULID_A, Sample.LATER))
        assertRoundTrips(CreateShareGrantResponse(Sample.ULID_A))
        assertRoundTrips(AcceptShareGrantResponse(Sample.ULID_A, GrantStatus.ACTIVE))
        assertRoundTrips(
            Subscriber(
                userId = Sample.ULID_A,
                name = "Kamala",
                phoneMasked = Sample.PHONE_MASKED,
                status = GrantStatus.UNSUBSCRIBED,
                grantedAt = Sample.AT,
            ),
        )
        assertRoundTrips(RequestVehicleAccessRequest(Sample.ULID_A))
        assertRoundTrips(
            RequestVehicleAccessResponse(Sample.ULID_A, AccessRequestStatus.PENDING),
        )
    }

    // ---- trip-state.yaml ---------------------------------------------------------------------

    @Test
    fun the_tracking_session_dtos_round_trip() {
        assertRoundTrips(
            Session(
                sessionId = Sample.ULID_A,
                vehicleId = Sample.ULID_B,
                driverId = Sample.ULID_C,
                mode = ServiceMode.A,
                routeId = Sample.ULID_A,
                state = SessionState.AUTO_ENDED,
                autoEndAtDestination = true,
                startedAt = Sample.AT,
                endedAt = Sample.LATER,
                endReason = SessionEndReason.MQTT_OFFLINE,
                restartableUntil = Sample.LATER,
            ),
        )
        assertRoundTrips(
            StartSessionRequest(
                vehicleId = Sample.ULID_A,
                mode = ServiceMode.B,
                routeId = Sample.ULID_B,
                autoEndAtDestination = false,
            ),
        )
        assertRoundTrips(AutoEndSessionRequest(AutoEndReason.IDLE_TIMEOUT))
        assertRoundTrips(RatingInput(stars = 5, text = "On time"))
        assertRoundTrips(DriverRatingInput(stars = 4, text = "Polite", passengerId = Sample.ULID_A))
        assertRoundTrips(Rating(Sample.ULID_A, stars = 5, text = "On time", createdAt = Sample.AT))
    }
}
