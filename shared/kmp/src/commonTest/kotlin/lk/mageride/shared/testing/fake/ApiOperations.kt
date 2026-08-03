package lk.mageride.shared.testing.fake

import kotlinx.serialization.KSerializer
import kotlinx.serialization.serializer
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.models.CallbackAck
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.comms.AcknowledgeNotificationRequest
import lk.mageride.shared.data.models.comms.IssueVoipTokenRequest
import lk.mageride.shared.data.models.comms.NotificationPreferences
import lk.mageride.shared.data.models.comms.RecordCallOutcomeRequest
import lk.mageride.shared.data.models.comms.RegisterPushTokenRequest
import lk.mageride.shared.data.models.comms.SendNotificationRequest
import lk.mageride.shared.data.models.comms.SendNotificationResponse
import lk.mageride.shared.data.models.comms.StartCallRequest
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.comms.VoipTokenResponse
import lk.mageride.shared.data.models.content.AuthoredFaqListResponse
import lk.mageride.shared.data.models.content.BroadcastListResponse
import lk.mageride.shared.data.models.content.NotificationTemplate
import lk.mageride.shared.data.models.content.NotificationTemplateVersion
import lk.mageride.shared.data.models.content.OnboardingSlidesResponse
import lk.mageride.shared.data.models.content.OperatingCityListResponse
import lk.mageride.shared.data.models.content.UpdateNotificationTemplateRequest
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
import lk.mageride.shared.data.models.dispatch.OutstandingPenalties
import lk.mageride.shared.data.models.dispatch.PresenceResponse
import lk.mageride.shared.data.models.dispatch.ReportDriverNoShowRequest
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.SetDirectionalFilterRequest
import lk.mageride.shared.data.models.dispatch.SettlePenaltiesRequest
import lk.mageride.shared.data.models.dispatch.SettledPenalties
import lk.mageride.shared.data.models.fare.CalculateFinalFareRequest
import lk.mageride.shared.data.models.fare.ClaimDriverQrRequest
import lk.mageride.shared.data.models.fare.ConfirmDriverQrRequest
import lk.mageride.shared.data.models.fare.DisputeDriverQrRequest
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.fare.FinalFareResponse
import lk.mageride.shared.data.models.fare.InitiatePaymentRequest
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.fare.ProviderCallback
import lk.mageride.shared.data.models.fare.RefundFareRequest
import lk.mageride.shared.data.models.fare.RefundResponse
import lk.mageride.shared.data.models.fare.ScanDriverQrRequest
import lk.mageride.shared.data.models.iam.AuthSessionResponse
import lk.mageride.shared.data.models.iam.DefaultPaymentMethodPreference
import lk.mageride.shared.data.models.iam.DeleteAccountResponse
import lk.mageride.shared.data.models.iam.EffectivePermissions
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactInput
import lk.mageride.shared.data.models.iam.EmergencyContactListResponse
import lk.mageride.shared.data.models.iam.IdTokenLogin
import lk.mageride.shared.data.models.iam.IssueMqttTokenRequest
import lk.mageride.shared.data.models.iam.IssueMqttTokenResponse
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.LoginBootstrap
import lk.mageride.shared.data.models.iam.LookupUserResponse
import lk.mageride.shared.data.models.iam.OperatingCityPreference
import lk.mageride.shared.data.models.iam.PasswordLogin
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
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.data.models.query.PlaceSearchResponse
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.data.models.query.TransportOptionsResponse
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.registry.AcceptShareGrantResponse
import lk.mageride.shared.data.models.registry.BindVehicleDeviceRequest
import lk.mageride.shared.data.models.registry.BindVehicleDeviceResponse
import lk.mageride.shared.data.models.registry.CreateShareGrantRequest
import lk.mageride.shared.data.models.registry.CreateShareGrantResponse
import lk.mageride.shared.data.models.registry.DriverPayoutProfile
import lk.mageride.shared.data.models.registry.OnboardingStepInput
import lk.mageride.shared.data.models.registry.RegisterVehicleResponse
import lk.mageride.shared.data.models.registry.RequestVehicleAccessRequest
import lk.mageride.shared.data.models.registry.RequestVehicleAccessResponse
import lk.mageride.shared.data.models.registry.SaveOnboardingStepResponse
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.UpdateVehicleDriverProfileRequest
import lk.mageride.shared.data.models.registry.UploadedPayoutDocument
import lk.mageride.shared.data.models.registry.UpsertDriverPayoutProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileResponse
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.registry.VehicleRegistration
import lk.mageride.shared.data.models.registry.VehicleStatusResponse
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.AcceptRideOfferResponse
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.CompleteRideResponse
import lk.mageride.shared.data.models.ride.ConfirmCashOnDeliveryRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.DeclineRideOfferRequest
import lk.mageride.shared.data.models.ride.DisputeRideRequest
import lk.mageride.shared.data.models.ride.ExpireRideOfferRequest
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.MarkRideMatchingRequest
import lk.mageride.shared.data.models.ride.NotifyPaymentSettledRequest
import lk.mageride.shared.data.models.ride.OfferPlaced
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.ride.PlaceRideOfferRequest
import lk.mageride.shared.data.models.ride.ProofArtifactResponse
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.RideSagaState
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.SystemCancelRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import lk.mageride.shared.data.models.safety.BlockDriverRequest
import lk.mageride.shared.data.models.safety.ReportVehicleRequest
import lk.mageride.shared.data.models.safety.SharedTripView
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosEvent
import lk.mageride.shared.data.models.safety.TriggerSosRequest
import lk.mageride.shared.data.models.safety.TripShareLink
import lk.mageride.shared.data.models.safety.VehicleReport
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.AccessRequestAccepted
import lk.mageride.shared.data.models.subscription.ChargeDailyFeeRequest
import lk.mageride.shared.data.models.subscription.CreditTransfer
import lk.mageride.shared.data.models.subscription.DailyFeeCharge
import lk.mageride.shared.data.models.subscription.DailyFeeRateList
import lk.mageride.shared.data.models.subscription.FeeRefundRequest
import lk.mageride.shared.data.models.subscription.FeeRefundRequestList
import lk.mageride.shared.data.models.subscription.MarkSubscriberCashPaidRequest
import lk.mageride.shared.data.models.subscription.PayModeBSubscriptionRequest
import lk.mageride.shared.data.models.subscription.PurchaseVoucherRequest
import lk.mageride.shared.data.models.subscription.RejectAccessRequest
import lk.mageride.shared.data.models.subscription.RequestCreditTransferRequest
import lk.mageride.shared.data.models.subscription.RequestDailyFeeRefundRequest
import lk.mageride.shared.data.models.subscription.RequestModeBAccessRequest
import lk.mageride.shared.data.models.subscription.SendCreditToDriverRequest
import lk.mageride.shared.data.models.subscription.SetSubscriberFareRequest
import lk.mageride.shared.data.models.subscription.SubscriberRow
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionProviderCallback
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.subscription.VoucherDiscountTierList
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqListResponse
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketRef
import lk.mageride.shared.data.models.support.UploadedScreenshot
import lk.mageride.shared.data.models.transit.FeedUploadStatus
import lk.mageride.shared.data.models.transit.FeedVersion
import lk.mageride.shared.data.models.transit.GtfsUploadAccepted
import lk.mageride.shared.data.models.transit.GtfsValidationReport
import lk.mageride.shared.data.models.transit.ImportGtfsFeedRequest
import lk.mageride.shared.data.models.transit.ImportGtfsFeedResponse
import lk.mageride.shared.data.models.transit.ParsedMapsLink
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitRoute
import lk.mageride.shared.data.models.trip.AutoEndSessionRequest
import lk.mageride.shared.data.models.trip.DriverRatingInput
import lk.mageride.shared.data.models.trip.Rating
import lk.mageride.shared.data.models.trip.RatingInput
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.StartSessionRequest
import lk.mageride.shared.data.models.version.AppVersionCheck
import lk.mageride.shared.data.models.wallet.InitiateWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.LankaqrTopupRequest
import lk.mageride.shared.data.models.wallet.OnepayTopupRequest
import lk.mageride.shared.data.models.wallet.PurchaseVoucherFromWalletRequest
import lk.mageride.shared.data.models.wallet.RequestWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupCallback
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.VoucherDiscountTierUsageList
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.data.models.wallet.WalletVoucherPurchase

/**
 * Every operation `backend/contracts/` declares and [lk.mageride.shared.data.api.MageRideApi]
 * implements, as data: the route, the success status, and the DTO the response body decodes into.
 *
 * This is the table [FakeApiBackend] answers from, which is what lets *one* fake cover all 176
 * operations instead of a stub per screen. It is also the table `ApiOperationTableTest` walks: the
 * id, verb and path of every row are asserted against the YAML, so an operation added, moved or
 * renamed in a contract fails the build here rather than going unfaked.
 *
 * The response column is the **typed client's own return type**, not a guess — `requestRide`
 * answers a `RequestRideResponse` because `RideApi.requestRide` is declared to. That is what makes
 * "the fake has the same surface as the real client" true by construction rather than by review:
 * a body this table synthesises always decodes into exactly what the caller is handed.
 *
 * Rows with no response serializer carry no body — the eleven `204`s, plus `downloadGtfsFeed`,
 * which is a `302` whose payload is its `Location` header. The request column is filled in for the
 * 85 operations that take a JSON body, and is what `ContractShapeTest` validates the outbound half
 * against; the rest carry their input in the path or the query string.
 */
internal object ApiOperations {

    /** Every operation, grouped by service in `ApiService` declaration order. */
    val ALL: List<FakeOperation> = listOf(
        // iam-svc — auth, profile, session, saved addresses (20)
        op<RequestOtpResponse>(
            "requestOtp",
            ApiService.IAM,
            "POST",
            "/v1/auth/otp/request",
            200,
            sends<RequestOtpRequest>(),
        ),
        op<VerifyOtpResponse>(
            "verifyOtp",
            ApiService.IAM,
            "POST",
            "/v1/auth/otp/verify",
            200,
            sends<VerifyOtpRequest>(),
        ),
        op<ResendOtpResponse>(
            "resendOtp",
            ApiService.IAM,
            "POST",
            "/v1/auth/otp/resend",
            200,
            sends<ResendOtpRequest>(),
        ),
        op<TokenPair>(
            "refreshSession",
            ApiService.IAM,
            "POST",
            "/v1/auth/refresh",
            200,
            sends<RefreshSessionRequest>(),
        ),
        noBody("logout", ApiService.IAM, "POST", "/v1/auth/logout", 204),
        op<AuthSessionResponse>(
            "signInWithGoogle",
            ApiService.IAM,
            "POST",
            "/v1/auth/google",
            200,
            sends<IdTokenLogin>(),
        ),
        op<AuthSessionResponse>(
            "signInWithApple",
            ApiService.IAM,
            "POST",
            "/v1/auth/apple",
            200,
            sends<IdTokenLogin>(),
        ),
        op<AuthSessionResponse>(
            "signInWithPassword",
            ApiService.IAM,
            "POST",
            "/v1/auth/password",
            200,
            sends<PasswordLogin>(),
        ),
        op<AuthSessionResponse>(
            "adminLogin",
            ApiService.IAM,
            "POST",
            "/v1/admin/auth/login",
            200,
            sends<PasswordLogin>(),
        ),
        op<IssueMqttTokenResponse>(
            "issueMqttToken",
            ApiService.IAM,
            "POST",
            "/v1/auth/mqtt-token",
            200,
            sends<IssueMqttTokenRequest>(),
        ),
        op<UserProfile>("getMyProfile", ApiService.IAM, "GET", "/v1/users/me", 200),
        op<UserProfile>("updateMyProfile", ApiService.IAM, "PUT", "/v1/users/me", 200, sends<UpdateProfileRequest>()),
        op<DeleteAccountResponse>("deleteMyAccount", ApiService.IAM, "DELETE", "/v1/users/me", 202),
        op<LookupUserResponse>("lookupUserByPhone", ApiService.IAM, "GET", "/v1/users/lookup", 200),
        op<SavedAddressListResponse>("listSavedAddresses", ApiService.IAM, "GET", "/v1/me/saved-addresses", 200),
        op<SavedAddress>(
            "createSavedAddress",
            ApiService.IAM,
            "POST",
            "/v1/me/saved-addresses",
            201,
            sends<SavedAddressInput>(),
        ),
        op<SavedAddress>(
            "updateSavedAddress",
            ApiService.IAM,
            "PUT",
            "/v1/me/saved-addresses/{addressId}",
            200,
            sends<SavedAddressInput>(),
        ),
        noBody("deleteSavedAddress", ApiService.IAM, "DELETE", "/v1/me/saved-addresses/{addressId}", 204),
        op<LanguagePreference>(
            "setLanguagePreference",
            ApiService.IAM,
            "PUT",
            "/v1/me/prefs/language",
            200,
            sends<LanguagePreference>(),
        ),
        op<OperatingCityPreference>(
            "setOperatingCity",
            ApiService.IAM,
            "PUT",
            "/v1/me/prefs/operating-city",
            200,
            sends<OperatingCityPreference>(),
        ),

        // registry-svc — driver identity, vehicles, onboarding, sharing (17)
        op<UpsertDriverProfileResponse>(
            "upsertDriverProfile",
            ApiService.REGISTRY,
            "PUT",
            "/v1/drivers/profile",
            200,
            sends<UpsertDriverProfileRequest>(),
        ),
        op<RegisterVehicleResponse>(
            "registerVehicle",
            ApiService.REGISTRY,
            "POST",
            "/v1/vehicles",
            201,
            sends<VehicleRegistration>(),
        ),
        op<VehicleListResponse>("listMyVehicles", ApiService.REGISTRY, "GET", "/v1/vehicles/mine", 200),
        op<VehicleDetail>("getVehicle", ApiService.REGISTRY, "GET", "/v1/vehicles/{vehicleId}", 200),
        op<VehicleStatusResponse>(
            "getVehicleStatus",
            ApiService.REGISTRY,
            "GET",
            "/v1/vehicles/{vehicleId}/status",
            200,
        ),
        op<VehicleOnboardingStatusResponse>(
            "getVehicleOnboardingStatus",
            ApiService.REGISTRY,
            "GET",
            "/v1/vehicles/{vehicleId}/onboarding-status",
            200,
        ),
        op<SaveOnboardingStepResponse>(
            "saveVehicleOnboardingStep",
            ApiService.REGISTRY,
            "PUT",
            "/v1/vehicles/{vehicleId}/onboarding/{step}",
            200,
            sends<OnboardingStepInput>(),
        ),
        noBody("deactivateVehicle", ApiService.REGISTRY, "POST", "/v1/vehicles/{vehicleId}/deactivate", 204),
        op<VehicleDetail>(
            "updateVehicleDriverProfile",
            ApiService.REGISTRY,
            "PUT",
            "/v1/vehicles/{vehicleId}/driver-profile",
            200,
            sends<UpdateVehicleDriverProfileRequest>(),
        ),
        op<BindVehicleDeviceResponse>(
            "bindVehicleDevice",
            ApiService.REGISTRY,
            "POST",
            "/v1/vehicles/{vehicleId}/device",
            201,
            sends<BindVehicleDeviceRequest>(),
        ),
        op<CreateShareGrantResponse>(
            "createShareGrant",
            ApiService.REGISTRY,
            "POST",
            "/v1/vehicles/{vehicleId}/share",
            201,
            sends<CreateShareGrantRequest>(),
        ),
        op<AcceptShareGrantResponse>(
            "acceptShareGrant",
            ApiService.REGISTRY,
            "POST",
            "/v1/vehicles/{vehicleId}/share/{grantId}/accept",
            200,
        ),
        noBody("revokeShareGrant", ApiService.REGISTRY, "DELETE", "/v1/vehicles/{vehicleId}/share/{grantId}", 204),
        op<Page<Subscriber>>(
            "listVehicleSubscribers",
            ApiService.REGISTRY,
            "GET",
            "/v1/vehicles/{vehicleId}/subscribers",
            200,
        ),
        noBody(
            "unsubscribeFromVehicle",
            ApiService.REGISTRY,
            "DELETE",
            "/v1/vehicles/{vehicleId}/subscribers/{userId}",
            204,
        ),
        op<RequestVehicleAccessResponse>(
            "requestVehicleAccess",
            ApiService.REGISTRY,
            "POST",
            "/v1/share-requests",
            201,
            sends<RequestVehicleAccessRequest>(),
        ),

        // trip-state-svc — Mode A/B tracking sessions (never a Mode C ride) (7)
        op<Session>(
            "startSession",
            ApiService.TRIP_STATE,
            "POST",
            "/v1/sessions/start",
            201,
            sends<StartSessionRequest>(),
        ),
        op<Session>("endSession", ApiService.TRIP_STATE, "POST", "/v1/sessions/{sessionId}/end", 200),
        op<Session>("restartSession", ApiService.TRIP_STATE, "POST", "/v1/sessions/{sessionId}/restart", 200),
        op<Session>("getActiveSession", ApiService.TRIP_STATE, "GET", "/v1/sessions/{vehicleId}/active", 200),
        op<Rating>(
            "ratePassengerJourney",
            ApiService.TRIP_STATE,
            "POST",
            "/v1/sessions/{sessionId}/rating",
            201,
            sends<RatingInput>(),
        ),
        op<Rating>(
            "rateSessionPassenger",
            ApiService.TRIP_STATE,
            "POST",
            "/v1/sessions/{sessionId}/driver-rating",
            201,
            sends<DriverRatingInput>(),
        ),
        op<Session>(
            "autoEndSession",
            ApiService.TRIP_STATE,
            "POST",
            "/v1/internal/sessions/{sessionId}/auto-end",
            200,
            sends<AutoEndSessionRequest>(),
        ),

        // ride-svc — the Mode C ride aggregate (24)
        op<RequestRideResponse>("requestRide", ApiService.RIDE, "POST", "/v1/rides/request", 202, sends<RideRequest>()),
        op<Page<RideHistoryRow>>("listRideHistory", ApiService.RIDE, "GET", "/v1/rides/history", 200),
        op<RideDetail>(
            "getActivePassengerRide",
            ApiService.RIDE,
            "GET",
            "/v1/rides/passenger/{passengerId}/active",
            200,
        ),
        op<RideDetail>("getActiveDriverRide", ApiService.RIDE, "GET", "/v1/rides/driver/{driverId}/active", 200),
        op<RideDetail>("getRide", ApiService.RIDE, "GET", "/v1/rides/{rideId}", 200),
        op<RideStateSnapshot>("getRideState", ApiService.RIDE, "GET", "/v1/rides/{rideId}/state", 200),
        op<AcceptRideOfferResponse>(
            "acceptRideOffer",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/offer/{driverId}/accept",
            200,
            sends<AcceptRideOfferRequest>(),
        ),
        op<RideStateChange>(
            "declineRideOffer",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/offer/{driverId}/decline",
            200,
            sends<DeclineRideOfferRequest>(),
        ),
        op<RideStateChange>(
            "markDriverArrived",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/arrive",
            200,
            sends<VersionedCommand>(),
        ),
        op<RideStateChange>(
            "startRide",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/start",
            200,
            sends<StartRideRequest>(),
        ),
        op<CompleteRideResponse>(
            "completeRide",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/complete",
            200,
            sends<VersionedCommand>(),
        ),
        op<CancelRideResponse>(
            "cancelRide",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/cancel",
            200,
            sends<CancelRideRequest>(),
        ),
        op<TicketRef>(
            "disputeRide",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/dispute",
            201,
            sends<DisputeRideRequest>(),
        ),
        op<RideStateChange>(
            "verifyPackagePickupOtp",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/package/pickup-otp",
            200,
            sends<OtpAttempt>(),
        ),
        op<RideStateChange>(
            "verifyPackageDeliveryOtp",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/package/delivery-otp",
            200,
            sends<OtpAttempt>(),
        ),
        op<ProofArtifactResponse>(
            "uploadPackageProofPhoto",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/package/proof-photo",
            201,
        ),
        op<RideStateChange>(
            "confirmCashOnDelivery",
            ApiService.RIDE,
            "POST",
            "/v1/rides/{rideId}/cod-collected",
            200,
            sends<ConfirmCashOnDeliveryRequest>(),
        ),
        op<CreateLocationRequestResponse>(
            "createLocationRequest",
            ApiService.RIDE,
            "POST",
            "/v1/location-requests",
            202,
            sends<CreateLocationRequestRequest>(),
        ),
        op<LocationRequest>("getLocationRequest", ApiService.RIDE, "GET", "/v1/location-requests/{requestId}", 200),
        op<LocationRequest>(
            "confirmLocationRequest",
            ApiService.RIDE,
            "POST",
            "/v1/location-requests/{requestId}/confirm",
            200,
            sends<GeoPointWithAccuracy>(),
        ),
        op<LocationRequest>(
            "declineLocationRequest",
            ApiService.RIDE,
            "POST",
            "/v1/location-requests/{requestId}/decline",
            200,
        ),
        // The three commands dispatch-svc drives (Δ C022/C023). Added by C025 — the operations
        // reached `ride.yaml` without the matching client rows, so this table was three short.
        op<RideStateChange>(
            "markRideMatching",
            ApiService.RIDE,
            "POST",
            "/v1/internal/rides/{rideId}/matching",
            200,
            sends<MarkRideMatchingRequest>(),
        ),
        op<OfferPlaced>(
            "placeRideOffer",
            ApiService.RIDE,
            "POST",
            "/v1/internal/rides/{rideId}/offer",
            200,
            sends<PlaceRideOfferRequest>(),
        ),
        op<RideStateChange>(
            "expireRideOffer",
            ApiService.RIDE,
            "POST",
            "/v1/internal/rides/{rideId}/offer/expire",
            200,
            sends<ExpireRideOfferRequest>(),
        ),
        op<RideStateChange>(
            "systemCancelRide",
            ApiService.RIDE,
            "POST",
            "/v1/internal/rides/{rideId}/system-cancel",
            200,
            sends<SystemCancelRideRequest>(),
        ),
        op<RideStateChange>(
            "notifyPaymentSettled",
            ApiService.RIDE,
            "POST",
            "/v1/internal/rides/{rideId}/payment-settled",
            200,
            sends<NotifyPaymentSettledRequest>(),
        ),
        op<RideSagaState>("getRideSagaState", ApiService.RIDE, "GET", "/v1/internal/rides/{rideId}/saga-state", 200),

        // dispatch-svc — presence, Directional, Job Board, Driver Level (15)
        op<PresenceResponse>(
            "goOnline",
            ApiService.DISPATCH,
            "POST",
            "/v1/standby/online",
            200,
            sends<GoOnlineRequest>(),
        ),
        op<PresenceResponse>("goOffline", ApiService.DISPATCH, "POST", "/v1/standby/offline", 200),
        op<DirectionalFilterState>("getDirectionalFilter", ApiService.DISPATCH, "GET", "/v1/standby/directional", 200),
        op<DirectionalFilterCreated>(
            "setDirectionalFilter",
            ApiService.DISPATCH,
            "POST",
            "/v1/standby/directional",
            201,
            sends<SetDirectionalFilterRequest>(),
        ),
        op<DirectionalFilterCleared>(
            "clearDirectionalFilter",
            ApiService.DISPATCH,
            "DELETE",
            "/v1/standby/directional",
            200,
        ),
        op<ScheduledRide>(
            "scheduleRide",
            ApiService.DISPATCH,
            "POST",
            "/v1/rides/schedule",
            201,
            sends<ScheduleRideRequest>(),
        ),
        noBody("cancelScheduledRide", ApiService.DISPATCH, "DELETE", "/v1/rides/schedule/{scheduledRideId}", 204),
        op<Page<ScheduledRide>>(
            "listDriverScheduledRides",
            ApiService.DISPATCH,
            "GET",
            "/v1/rides/scheduled/{driverId}",
            200,
        ),
        op<Page<ScheduledRide>>("listJobBoard", ApiService.DISPATCH, "GET", "/v1/rides/job-board", 200),
        op<JobBoardIntentResponse>(
            "postJobBoardIntent",
            ApiService.DISPATCH,
            "POST",
            "/v1/rides/job-board/{rideId}/intent",
            200,
        ),
        op<DriverLevelResponse>("getDriverLevel", ApiService.DISPATCH, "GET", "/v1/drivers/{driverId}/level", 200),
        op<DriverStatsResponse>("getDriverStats", ApiService.DISPATCH, "GET", "/v1/drivers/{driverId}/stats", 200),
        op<DriverLevelAfterNoShow>(
            "reportDriverNoShow",
            ApiService.DISPATCH,
            "POST",
            "/v1/internal/drivers/{driverId}/no-show",
            200,
            sends<ReportDriverNoShowRequest>(),
        ),
        op<DirectionalConfig>(
            "updateDirectionalConfig",
            ApiService.DISPATCH,
            "PUT",
            "/v1/admin/dispatch/directional-config",
            200,
            sends<DirectionalConfig>(),
        ),
        op<LevelConfig>(
            "updateDriverLevelConfig",
            ApiService.DISPATCH,
            "PUT",
            "/v1/admin/drivers/level-config",
            200,
            sends<LevelConfig>(),
        ),

        // fare-svc — estimates, final fare, payments (12)
        op<FareEstimateResponse>("estimateFare", ApiService.FARE, "GET", "/v1/fare/estimate", 200),
        op<FinalFareResponse>(
            "calculateFinalFare",
            ApiService.FARE,
            "POST",
            "/v1/fare/calculate",
            200,
            sends<CalculateFinalFareRequest>(),
        ),
        op<PaymentInitiation>(
            "initiatePayment",
            ApiService.FARE,
            "POST",
            "/v1/fare/pay",
            200,
            sends<InitiatePaymentRequest>(),
        ),
        op<PaymentStatus>("getPaymentStatus", ApiService.FARE, "GET", "/v1/fare/pay/{paymentId}/status", 200),
        op<PaymentStatus>("fallbackToCash", ApiService.FARE, "POST", "/v1/fare/pay/{paymentId}/fallback-cash", 200),
        op<PaymentStatus>(
            "payByScanningDriverQr",
            ApiService.FARE,
            "POST",
            "/v1/fare/pay/scan-driver-qr",
            200,
            sends<ScanDriverQrRequest>(),
        ),
        op<PaymentStatus>(
            "claimDriverQrPayment",
            ApiService.FARE,
            "POST",
            "/v1/fare/pay/driver-qr/claim",
            202,
            sends<ClaimDriverQrRequest>(),
        ),
        op<PaymentStatus>(
            "confirmDriverQrPayment",
            ApiService.FARE,
            "POST",
            "/v1/fare/pay/driver-qr/confirm",
            200,
            sends<ConfirmDriverQrRequest>(),
        ),
        op<TicketRef>(
            "disputeDriverQrPayment",
            ApiService.FARE,
            "POST",
            "/v1/fare/pay/driver-qr/dispute",
            201,
            sends<DisputeDriverQrRequest>(),
        ),
        op<RefundResponse>(
            "refundFare",
            ApiService.FARE,
            "POST",
            "/v1/admin/fare/refund",
            201,
            sends<RefundFareRequest>(),
        ),

        // subscription-svc — daily fee, credit, vouchers, Mode B subscriptions (29)
        op<DailyFeeRateList>("listDailyFeeRates", ApiService.SUBSCRIPTION, "GET", "/v1/fees/rates", 200),
        op<TodaysDailyFee>("getTodaysDailyFee", ApiService.SUBSCRIPTION, "GET", "/v1/fees/{driverId}/today", 200),
        op<Page<DailyFeeCharge>>(
            "listDailyFeeHistory",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/fees/{driverId}/history",
            200,
        ),
        op<DailyFeeCharge>(
            "chargeDailyFeeBeforeTrip",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/internal/fees/{driverId}/charge-before-trip",
            200,
            sends<ChargeDailyFeeRequest>(),
        ),
        op<CreditTransfer>(
            "requestCreditTransfer",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/subscriptions/credit-transfer/request",
            201,
            sends<RequestCreditTransferRequest>(),
        ),
        op<Page<CreditTransfer>>(
            "listPendingCreditTransfers",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/subscriptions/credit-transfer/pending",
            200,
        ),
        op<CreditTransfer>(
            "approveCreditTransfer",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/subscriptions/credit-transfer/{transferId}/approve",
            200,
        ),
        op<CreditTransfer>(
            "rejectCreditTransfer",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/subscriptions/credit-transfer/{transferId}/reject",
            200,
        ),
        op<CreditTransfer>(
            "sendCreditToDriver",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/transfers/driver",
            201,
            sends<SendCreditToDriverRequest>(),
        ),
        op<VoucherPurchase>(
            "purchaseVoucher",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/vouchers/purchase",
            201,
            sends<PurchaseVoucherRequest>(),
        ),
        op<Page<AccessRequest>>(
            "listModeBAccessRequests",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/mode-b/{vehicleId}/access-requests",
            200,
        ),
        op<AccessRequest>(
            "requestModeBAccess",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/{vehicleId}/access-requests",
            201,
            sends<RequestModeBAccessRequest>(),
        ),
        op<AccessRequestAccepted>(
            "acceptModeBAccessRequest",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/access-requests/{requestId}/accept",
            200,
        ),
        op<AccessRequest>(
            "rejectModeBAccessRequest",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/access-requests/{requestId}/reject",
            200,
            sends<RejectAccessRequest>(),
        ),
        op<Page<Subscription>>(
            "listPassengerSubscriptions",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/mode-b/subscriptions/{passengerId}",
            200,
        ),
        op<Subscription>(
            "unsubscribeModeB",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/subscriptions/{subscriptionId}/unsubscribe",
            200,
        ),
        op<SubscriptionPayment>(
            "payModeBSubscription",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/subscriptions/{subscriptionId}/pay",
            200,
            sends<PayModeBSubscriptionRequest>(),
        ),
        op<Page<SubscriptionPayment>>(
            "listSubscriptionPayments",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/mode-b/subscriptions/{subscriptionId}/payments",
            200,
        ),
        op<SubscriptionPayment>(
            "uploadTransferSlip",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/payments/{paymentId}/transfer-slip",
            200,
        ),
        op<SubscriptionPayment>(
            "confirmTransferSlip",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/payments/{paymentId}/confirm",
            200,
        ),
        op<CallbackAck>(
            "modeBLankaqrConfirm",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/pay/lankaqr/confirm",
            200,
            sends<SubscriptionProviderCallback>(),
        ),
        op<Page<SubscriberRow>>(
            "listModeBSubscribers",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/mode-b/{vehicleId}/subscribers",
            200,
        ),
        noBody(
            "deleteModeBSubscriber",
            ApiService.SUBSCRIPTION,
            "DELETE",
            "/v1/mode-b/{vehicleId}/subscribers/{subscriberId}",
            204,
        ),
        op<SubscriberRow>(
            "setSubscriberFare",
            ApiService.SUBSCRIPTION,
            "PUT",
            "/v1/mode-b/{vehicleId}/subscribers/{subscriberId}/fare",
            200,
            sends<SetSubscriberFareRequest>(),
        ),
        op<SubscriptionPayment>(
            "markSubscriberCashPaid",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/mode-b/{vehicleId}/subscribers/{subscriberId}/mark-cash",
            200,
            sends<MarkSubscriberCashPaidRequest>(),
        ),
        op<Page<SubscriptionPayment>>(
            "listSubscriberPayments",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/mode-b/{vehicleId}/subscribers/{subscriberId}/payments",
            200,
        ),
        op<DailyFeeRateList>(
            "updateDailyFeeRates",
            ApiService.SUBSCRIPTION,
            "PUT",
            "/v1/admin/fees/rates",
            200,
            sends<DailyFeeRateList>(),
        ),
        op<VoucherDiscountTierList>(
            "updateVoucherDiscountTiers",
            ApiService.SUBSCRIPTION,
            "PUT",
            "/v1/admin/voucher-discount-tiers",
            200,
            sends<VoucherDiscountTierList>(),
        ),

        // wallet-svc — balance, ledger, transfers, top-ups (11)
        op<Wallet>("getWallet", ApiService.WALLET, "GET", "/v1/wallet/{userId}", 200),
        op<Page<WalletTransaction>>(
            "listWalletTransactions",
            ApiService.WALLET,
            "GET",
            "/v1/wallet/{userId}/transactions",
            200,
        ),
        op<Page<TransferRow>>("listWalletTransfers", ApiService.WALLET, "GET", "/v1/wallet/{driverId}/transfers", 200),
        op<TransferRow>(
            "initiateWalletCreditTransfer",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/credit-transfer/initiate",
            201,
            sends<InitiateWalletCreditTransferRequest>(),
        ),
        op<VoucherDiscountTierList>(
            "listVoucherDiscountTiers",
            ApiService.WALLET,
            "GET",
            "/v1/wallet/voucher/discount-tiers",
            200,
        ),
        op<Topup>(
            "topupWithOnepay",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/topup/onepay",
            200,
            sends<OnepayTopupRequest>(),
        ),
        op<Topup>(
            "topupWithLankaqr",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/topup/lankaqr",
            200,
            sends<LankaqrTopupRequest>(),
        ),
        op<CallbackAck>(
            "onepayTopupWebhook",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/topup/onepay/webhook",
            200,
            sends<TopupCallback>(),
        ),
        op<CallbackAck>(
            "lankaqrTopupConfirm",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/topup/lankaqr/confirm",
            200,
            sends<TopupCallback>(),
        ),
        op<VoucherDiscountTierUsageList>(
            "adminListVoucherDiscountTiers",
            ApiService.WALLET,
            "GET",
            "/v1/wallet/admin/voucher-discount-tiers",
            200,
        ),
        op<VoucherDiscountTierList>(
            "adminUpdateVoucherDiscountTiers",
            ApiService.WALLET,
            "PUT",
            "/v1/wallet/admin/voucher-discount-tiers",
            200,
            sends<VoucherDiscountTierList>(),
        ),

        // query-svc — nearby, trips, earnings, geocoding (9)
        op<NearbyVehiclesResponse>("getNearbyVehicles", ApiService.QUERY, "GET", "/v1/nearby", 200),
        op<TransportOptionsResponse>("getTransportOptions", ApiService.QUERY, "GET", "/v1/transport-options", 200),
        op<NearbyVehiclesResponse>("getBusesOnRoute", ApiService.QUERY, "GET", "/v1/routes/{routeNumber}/buses", 200),
        op<Page<TripSummary>>("listTrips", ApiService.QUERY, "GET", "/v1/trips/{userId}", 200),
        op<TripDetail>("getTrip", ApiService.QUERY, "GET", "/v1/trips/{userId}/{tripId}", 200),
        op<EarningsSummary>("getDriverEarnings", ApiService.QUERY, "GET", "/v1/earnings/{driverId}", 200),
        op<Page<SessionEarning>>(
            "listEarningSessions",
            ApiService.QUERY,
            "GET",
            "/v1/earnings/{driverId}/sessions",
            200,
        ),
        op<PlaceSearchResponse>("searchPlaces", ApiService.QUERY, "GET", "/v1/geo/search", 200),
        op<GeocodedPlace>("reverseGeocode", ApiService.QUERY, "GET", "/v1/geo/reverse", 200),

        // transit-svc — GTFS planning and the Dataset Manager (10)
        op<TransitOptionsResponse>("getTransitOptions", ApiService.TRANSIT, "GET", "/v1/transit/options", 200),
        op<TransitRoute>("getTransitRoute", ApiService.TRANSIT, "GET", "/v1/transit/routes/{routeId}", 200),
        op<ParsedMapsLink>("parseMapsLink", ApiService.TRANSIT, "GET", "/v1/geo/parse-maps-link", 200),
        op<GtfsUploadAccepted>("uploadGtfsFeed", ApiService.TRANSIT, "POST", "/v1/admin/transit/gtfs/uploads", 202),
        op<FeedUploadStatus>(
            "getGtfsUpload",
            ApiService.TRANSIT,
            "GET",
            "/v1/admin/transit/gtfs/uploads/{feedVersionId}",
            200,
        ),
        op<GtfsValidationReport>(
            "getGtfsValidationReport",
            ApiService.TRANSIT,
            "GET",
            "/v1/admin/transit/gtfs/uploads/{feedVersionId}/report",
            200,
        ),
        op<FeedVersion>(
            "activateGtfsFeed",
            ApiService.TRANSIT,
            "POST",
            "/v1/admin/transit/gtfs/uploads/{feedVersionId}/activate",
            200,
        ),
        op<Page<FeedVersion>>("listGtfsVersions", ApiService.TRANSIT, "GET", "/v1/admin/transit/gtfs/versions", 200),
        noBody(
            "downloadGtfsFeed",
            ApiService.TRANSIT,
            "GET",
            "/v1/admin/transit/gtfs/versions/{feedVersionId}/download",
            302,
        ),
        op<ImportGtfsFeedResponse>(
            "importGtfsFeed",
            ApiService.TRANSIT,
            "POST",
            "/v1/admin/transit/gtfs-import",
            202,
            sends<ImportGtfsFeedRequest>(),
        ),

        // safety-svc — SOS, trip share, reports, blocks (8)
        op<SosDispatched>("triggerSos", ApiService.SAFETY, "POST", "/v1/sos", 200, sends<TriggerSosRequest>()),
        op<Page<SosEvent>>("listSosHistory", ApiService.SAFETY, "GET", "/v1/sos/{userId}/history", 200),
        op<TripShareLink>("createTripShare", ApiService.SAFETY, "POST", "/v1/trip-share/{tripId}", 201),
        noBody("revokeTripShare", ApiService.SAFETY, "DELETE", "/v1/trip-share/{tripId}", 204),
        op<SharedTripView>("getSharedTrip", ApiService.SAFETY, "GET", "/v1/trip-share/public/{token}", 200),
        op<VehicleReport>(
            "reportVehicle",
            ApiService.SAFETY,
            "POST",
            "/v1/reports/vehicle",
            201,
            sends<ReportVehicleRequest>(),
        ),
        noBody(
            "blockDriver",
            ApiService.SAFETY,
            "POST",
            "/v1/drivers/{driverId}/block",
            204,
            sends<BlockDriverRequest>(),
        ),
        noBody("unblockDriver", ApiService.SAFETY, "DELETE", "/v1/drivers/{driverId}/block", 204),

        // support-svc — FAQ and tickets (5)
        op<FaqListResponse>("listFaqArticles", ApiService.SUPPORT, "GET", "/v1/support/faq", 200),
        op<FaqArticle>("getFaqArticle", ApiService.SUPPORT, "GET", "/v1/support/faq/{articleId}", 200),
        op<Ticket>(
            "createSupportTicket",
            ApiService.SUPPORT,
            "POST",
            "/v1/support/tickets",
            201,
            sends<CreateSupportTicketRequest>(),
        ),
        op<Page<Ticket>>("listSupportTickets", ApiService.SUPPORT, "GET", "/v1/support/tickets/{userId}", 200),
        op<TicketDetail>("getSupportTicket", ApiService.SUPPORT, "GET", "/v1/support/tickets/{userId}/{ticketId}", 200),

        // content-svc — cities, templates, broadcasts (4)
        op<OperatingCityListResponse>("listOperatingCities", ApiService.CONTENT, "GET", "/v1/config/cities", 200),
        op<NotificationTemplate>(
            "renderNotificationTemplate",
            ApiService.CONTENT,
            "GET",
            "/v1/content/templates/{key}",
            200,
        ),
        op<BroadcastListResponse>("listActiveBroadcasts", ApiService.CONTENT, "GET", "/v1/content/broadcasts", 200),
        op<NotificationTemplateVersion>(
            "updateNotificationTemplate",
            ApiService.CONTENT,
            "PUT",
            "/v1/admin/content/{key}",
            200,
            sends<UpdateNotificationTemplateRequest>(),
        ),

        // voip-svc — call signalling (2)
        op<VoipTokenResponse>(
            "issueVoipToken",
            ApiService.VOIP,
            "POST",
            "/v1/voip/token",
            200,
            sends<IssueVoipTokenRequest>(),
        ),
        op<StartCallResponse>("startCall", ApiService.VOIP, "POST", "/v1/calls/start", 200, sends<StartCallRequest>()),

        // notification-svc — push tokens and preferences (3)
        noBody(
            "registerPushToken",
            ApiService.NOTIFICATION,
            "POST",
            "/v1/notify/register-token",
            204,
            sends<RegisterPushTokenRequest>(),
        ),
        op<NotificationPreferences>(
            "updateNotificationPreferences",
            ApiService.NOTIFICATION,
            "PUT",
            "/v1/notify/preferences",
            200,
            sends<NotificationPreferences>(),
        ),
        op<SendNotificationResponse>(
            "sendNotification",
            ApiService.NOTIFICATION,
            "POST",
            "/v1/internal/notify/send",
            202,
            sends<SendNotificationRequest>(),
        ),

        // version-check — the D-31 cold-start gate (1)
        op<OutstandingPenalties>(
            "listOutstandingPenalties",
            ApiService.DISPATCH,
            "GET",
            "/v1/internal/passengers/{passengerId}/penalties",
            200,
        ),
        op<SettledPenalties>(
            "settleOutstandingPenalties",
            ApiService.DISPATCH,
            "POST",
            "/v1/internal/passengers/{passengerId}/penalties/settle",
            200,
            sends<SettlePenaltiesRequest>(),
        ),
        noBody(
            "recordCallOutcome",
            ApiService.VOIP,
            "POST",
            "/v1/calls/{callId}/outcome",
            204,
            sends<RecordCallOutcomeRequest>(),
        ),
        noBody(
            "acknowledgeNotification",
            ApiService.NOTIFICATION,
            "POST",
            "/v1/notify/ack",
            204,
            sends<AcknowledgeNotificationRequest>(),
        ),
        op<AuthoredFaqListResponse>(
            "listAuthoredFaqArticles",
            ApiService.CONTENT,
            "GET",
            "/v1/content/faq",
            200,
        ),
        op<OnboardingSlidesResponse>(
            "listOnboardingSlides",
            ApiService.CONTENT,
            "GET",
            "/v1/content/onboarding/{audience}",
            200,
        ),
        op<UploadedScreenshot>(
            "uploadSupportScreenshot",
            ApiService.SUPPORT,
            "POST",
            "/v1/support/screenshots",
            201,
        ),
        op<TransferRow>(
            "requestWalletCreditTransfer",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/credit-transfer/request",
            201,
            sends<RequestWalletCreditTransferRequest>(),
        ),
        op<Page<TransferRow>>(
            "listPendingWalletCreditTransfers",
            ApiService.WALLET,
            "GET",
            "/v1/wallet/credit-transfer/pending",
            200,
        ),
        op<TransferRow>(
            "approveWalletCreditTransfer",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/credit-transfer/{transferId}/approve",
            200,
        ),
        op<TransferRow>(
            "rejectWalletCreditTransfer",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/credit-transfer/{transferId}/reject",
            200,
        ),
        op<WalletVoucherPurchase>(
            "purchaseVoucherFromWallet",
            ApiService.WALLET,
            "POST",
            "/v1/wallet/voucher/purchase",
            201,
            sends<PurchaseVoucherFromWalletRequest>(),
        ),
        op<Topup>("getTopup", ApiService.WALLET, "GET", "/v1/wallet/topup/{topupId}", 200),
        op<DefaultPaymentMethodPreference>(
            "setDefaultPaymentMethod",
            ApiService.IAM,
            "PUT",
            "/v1/me/prefs/payment-method",
            200,
            sends<DefaultPaymentMethodPreference>(),
        ),
        op<EmergencyContactListResponse>(
            "listEmergencyContacts",
            ApiService.IAM,
            "GET",
            "/v1/me/emergency-contacts",
            200,
        ),
        op<EmergencyContact>(
            "createEmergencyContact",
            ApiService.IAM,
            "POST",
            "/v1/me/emergency-contacts",
            201,
            sends<EmergencyContactInput>(),
        ),
        op<EmergencyContact>(
            "updateEmergencyContact",
            ApiService.IAM,
            "PUT",
            "/v1/me/emergency-contacts/{contactId}",
            200,
            sends<EmergencyContactInput>(),
        ),
        noBody(
            "deleteEmergencyContact",
            ApiService.IAM,
            "DELETE",
            "/v1/me/emergency-contacts/{contactId}",
            204,
        ),
        op<LoginBootstrap>("getLoginBootstrap", ApiService.IAM, "GET", "/v1/me/bootstrap", 200),
        op<EffectivePermissions>("getMyPermissions", ApiService.IAM, "GET", "/v1/me/permissions", 200),
        op<DriverPayoutProfile>(
            "getDriverPayoutProfile",
            ApiService.REGISTRY,
            "GET",
            "/v1/drivers/payout-profile",
            200,
        ),
        op<DriverPayoutProfile>(
            "upsertDriverPayoutProfile",
            ApiService.REGISTRY,
            "PUT",
            "/v1/drivers/payout-profile",
            200,
            sends<UpsertDriverPayoutProfileRequest>(),
        ),
        op<UploadedPayoutDocument>(
            "uploadDriverPayoutDocument",
            ApiService.REGISTRY,
            "POST",
            "/v1/drivers/payout-profile/documents",
            201,
        ),
        op<FeeRefundRequest>(
            "requestDailyFeeRefund",
            ApiService.SUBSCRIPTION,
            "POST",
            "/v1/fees/{driverId}/refund-requests",
            201,
            sends<RequestDailyFeeRefundRequest>(),
        ),
        op<FeeRefundRequestList>(
            "listDailyFeeRefundRequests",
            ApiService.SUBSCRIPTION,
            "GET",
            "/v1/fees/{driverId}/refund-requests",
            200,
        ),
        op<AppVersionCheck>("checkAppVersion", ApiService.VERSION, "GET", "/v1/version/check", 200),
    )

    /** Indexed by `operationId` — the key every request carries in its attributes. */
    val BY_ID: Map<String, FakeOperation> = ALL.associateBy { it.operationId }
}

/** One row of [ApiOperations], with the response type resolved to a serializer. */
@Suppress("LongParameterList")
private inline fun <reified T> op(
    operationId: String,
    service: ApiService,
    method: String,
    path: String,
    status: Int,
    request: KSerializer<*>? = null,
): FakeOperation = FakeOperation(operationId, service, method, path, status, serializer<T>(), request)

/** A row whose success response has no body — a `204`, or the `302` GTFS download. */
@Suppress("LongParameterList")
private fun noBody(
    operationId: String,
    service: ApiService,
    method: String,
    path: String,
    status: Int,
    request: KSerializer<*>? = null,
): FakeOperation = FakeOperation(operationId, service, method, path, status, null, request)

/** The request body’s serializer, for the operations that carry one. */
private inline fun <reified T> sends(): KSerializer<*> = serializer<T>()
