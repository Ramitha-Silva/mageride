package lk.mageride.shared.data.api.iam

import io.ktor.client.request.header
import io.ktor.client.request.parameter
import io.ktor.http.HttpHeaders
import kotlin.coroutines.cancellation.CancellationException
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.Credential
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiDelete
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.AuthSessionResponse
import lk.mageride.shared.data.models.iam.DefaultPaymentMethodPreference
import lk.mageride.shared.data.models.iam.DeleteAccountResponse
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

/**
 * iam-svc — auth, profile, session and saved addresses (`backend/contracts/iam.yaml`).
 *
 * **Sign-in is by surface (AL-07).** The passenger and driver apps use the phone-OTP trio only;
 * [signInWithGoogle], [signInWithApple], [signInWithPassword] and the admin pair exist because
 * `iam.yaml` declares them for the Fleet and Admin portals. There is no MFA anywhere — AL-37
 * removed it and the endpoints are deleted, not deprecated.
 *
 * C014 drives this client: it owns the session state machine, the token store and the
 * single-active-device-per-app rule (AL-08). Nothing here decides *when* to refresh — that is
 * [lk.mageride.shared.data.api.TokenProvider]'s job.
 */
@Suppress("TooManyFunctions")
public interface IamApi {

    /**
     * `POST /v1/auth/otp/request` — start a login and send an OTP by SMS.
     *
     * Public route, attested (D-30). Rate limited: `429 otp-rate-limited` on a 60-second resend
     * or the fifth attempt in an hour (D-32).
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun requestOtp(request: RequestOtpRequest, idempotencyKey: String? = null): RequestOtpResponse

    /**
     * `POST /v1/auth/otp/verify` — exchange the OTP for a session.
     *
     * `423 otp-locked` once the attempt budget is spent; `409 device-mismatch` when the
     * `deviceId` is not the one that requested the OTP.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun verifyOtp(request: VerifyOtpRequest, idempotencyKey: String? = null): VerifyOtpResponse

    /** `POST /v1/auth/otp/resend` — resend the OTP for an existing `authId`. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun resendOtp(request: ResendOtpRequest, idempotencyKey: String? = null): ResendOtpResponse

    /**
     * `POST /v1/auth/refresh` — rotate the refresh token and mint a new access token (D-29).
     *
     * The opaque token is presented **both** as the bearer credential and in the body, which is
     * what the contract's `refreshToken` security scheme asks for. It is single-use: presenting
     * a spent one revokes the whole session family, so exactly one refresh may be in flight.
     * The pipeline never auto-refreshes this call — that would recurse.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun refreshSession(request: RefreshSessionRequest, idempotencyKey: String? = null): TokenPair

    /** `POST /v1/auth/logout` — end this device's session. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun logout(idempotencyKey: String? = null)

    /** `POST /v1/auth/google` — Fleet Portal sign-in with a Google ID token. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun signInWithGoogle(request: IdTokenLogin, idempotencyKey: String? = null): AuthSessionResponse

    /** `POST /v1/auth/apple` — Fleet Portal sign-in with an Apple ID token. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun signInWithApple(request: IdTokenLogin, idempotencyKey: String? = null): AuthSessionResponse

    /** `POST /v1/auth/password` — Fleet Portal e-mail and password sign-in. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun signInWithPassword(request: PasswordLogin, idempotencyKey: String? = null): AuthSessionResponse

    /**
     * `POST /v1/admin/auth/login` with the `PasswordLogin` arm of the body's `oneOf`.
     *
     * Two named functions rather than two overloads: the body is `oneOf(PasswordLogin,
     * GoogleAuthCodeLogin)` (C012 decision 4), and a single all-nullable request type would
     * happily serialise a shape the server rejects.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun adminLoginWithPassword(
        request: PasswordLogin,
        idempotencyKey: String? = null,
    ): AuthSessionResponse

    /** `POST /v1/admin/auth/login` with the `GoogleAuthCodeLogin` arm. See [adminLoginWithPassword]. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun adminLoginWithGoogle(
        request: GoogleAuthCodeLogin,
        idempotencyKey: String? = null,
    ): AuthSessionResponse

    /**
     * `POST /v1/auth/mqtt-token` — mint the **separate** MQTT session JWT (E-02, D-21).
     *
     * Never the API access token: its TTL is `max(ride + 2 h, 4 h)` and it is bound to
     * `(vehicleId, deviceId, rideId?)`. C014 owns its renewal.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun issueMqttToken(
        request: IssueMqttTokenRequest,
        idempotencyKey: String? = null,
    ): IssueMqttTokenResponse

    /** `GET /v1/users/me` — the signed-in user. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getMyProfile(): UserProfile

    /** `PUT /v1/users/me` — update name, photo, language or notification switches. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun updateMyProfile(request: UpdateProfileRequest): UserProfile

    /** `DELETE /v1/users/me` — request erasure (E-06). `202`; the request id tracks the job. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun deleteMyAccount(): DeleteAccountResponse

    /**
     * `GET /v1/users/lookup` — is this number registered?
     *
     * **Service-to-service (mTLS).** Present so `iam.yaml` is covered end to end; the gateway
     * does not expose it to an app.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun lookupUserByPhone(phone: PhoneE164): LookupUserResponse

    /** `GET /v1/me/saved-addresses` — home, work and the rest. Not paginated by the contract. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listSavedAddresses(): SavedAddressListResponse

    /** `POST /v1/me/saved-addresses` — add one. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun createSavedAddress(request: SavedAddressInput, idempotencyKey: String? = null): SavedAddress

    /** `PUT /v1/me/saved-addresses/{addressId}` — replace one. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun updateSavedAddress(addressId: Ulid, request: SavedAddressInput): SavedAddress

    /** `DELETE /v1/me/saved-addresses/{addressId}` — remove one. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun deleteSavedAddress(addressId: Ulid)

    /** `PUT /v1/me/prefs/language` — set the UI language (D-26). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun setLanguagePreference(request: LanguagePreference): LanguagePreference

    /**
     * `PUT /v1/me/prefs/operating-city` — set the launch city chosen at onboarding (AL-27).
     *
     * The read side of AL-27 is content-svc's `GET /v1/config/cities`; this is the write side, and
     * it is what persists `iam.users.operating_city_code` (US-1.3a). The first-run screen offers
     * the city **before** there is a session, so the choice is held on the device and sent here on
     * the first authenticated call after sign-in.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun setOperatingCity(request: OperatingCityPreference): OperatingCityPreference

    /** `PUT /v1/me/prefs/payment-method` — the pre-selected method a booking uses (AL-14). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun setDefaultPaymentMethod(request: DefaultPaymentMethodPreference): DefaultPaymentMethodPreference

    /** `GET /v1/me/emergency-contacts` — who D-33's SOS SMS goes to (AL-13). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listEmergencyContacts(): EmergencyContactListResponse

    /** `POST /v1/me/emergency-contacts` — add one. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun createEmergencyContact(
        request: EmergencyContactInput,
        idempotencyKey: String? = null,
    ): EmergencyContact

    /** `PUT /v1/me/emergency-contacts/{contactId}` — correct a name or number. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun updateEmergencyContact(contactId: Ulid, request: EmergencyContactInput): EmergencyContact

    /** `DELETE /v1/me/emergency-contacts/{contactId}` — remove one. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun deleteEmergencyContact(contactId: Ulid)

    /**
     * `GET /v1/me/bootstrap` — everything a signed-in app needs before its first screen.
     *
     * **One round trip instead of seven** (AL-14, US-1.14/1.15, NFR-51): profile, addresses,
     * emergency contacts, payment default, any trip already in flight, the driver's shift, config
     * and the RBAC matrix.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getLoginBootstrap(): LoginBootstrap

    /**
     * `GET /v1/me/permissions` — the caller's effective RBAC set (AL-06).
     *
     * The union of every role held, deny-by-default. Areas with no grant come back present and
     * empty, so a client can render the whole matrix without knowing the row set.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getMyPermissions(): EffectivePermissions
}

@Suppress("TooManyFunctions")
internal class KtorIamApi(private val transport: ApiTransport) : IamApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun requestOtp(request: RequestOtpRequest, idempotencyKey: String?): RequestOtpResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "requestOtp",
            path = "/v1/auth/otp/request",
            idempotencyKey = idempotencyKey,
            attested = true,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun verifyOtp(request: VerifyOtpRequest, idempotencyKey: String?): VerifyOtpResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "verifyOtp",
            path = "/v1/auth/otp/verify",
            idempotencyKey = idempotencyKey,
            attested = true,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun resendOtp(request: ResendOtpRequest, idempotencyKey: String?): ResendOtpResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "resendOtp",
            path = "/v1/auth/otp/resend",
            idempotencyKey = idempotencyKey,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun refreshSession(request: RefreshSessionRequest, idempotencyKey: String?): TokenPair =
        transport.apiPost(
            service = SERVICE,
            operationId = "refreshSession",
            path = "/v1/auth/refresh",
            idempotencyKey = idempotencyKey,
            credential = Credential.PROVIDED,
        ) {
            header(HttpHeaders.Authorization, "Bearer ${request.refreshToken}")
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun logout(idempotencyKey: String?) {
        transport.apiPost(SERVICE, "logout", "/v1/auth/logout", idempotencyKey)
    }

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun signInWithGoogle(request: IdTokenLogin, idempotencyKey: String?): AuthSessionResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "signInWithGoogle",
            path = "/v1/auth/google",
            idempotencyKey = idempotencyKey,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun signInWithApple(request: IdTokenLogin, idempotencyKey: String?): AuthSessionResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "signInWithApple",
            path = "/v1/auth/apple",
            idempotencyKey = idempotencyKey,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun signInWithPassword(request: PasswordLogin, idempotencyKey: String?): AuthSessionResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "signInWithPassword",
            path = "/v1/auth/password",
            idempotencyKey = idempotencyKey,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun adminLoginWithPassword(request: PasswordLogin, idempotencyKey: String?): AuthSessionResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "adminLogin",
            path = ADMIN_LOGIN_PATH,
            idempotencyKey = idempotencyKey,
            credential = Credential.NONE,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun adminLoginWithGoogle(
        request: GoogleAuthCodeLogin,
        idempotencyKey: String?,
    ): AuthSessionResponse = transport.apiPost(
        service = SERVICE,
        operationId = "adminLogin",
        path = ADMIN_LOGIN_PATH,
        idempotencyKey = idempotencyKey,
        credential = Credential.NONE,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun issueMqttToken(
        request: IssueMqttTokenRequest,
        idempotencyKey: String?,
    ): IssueMqttTokenResponse = transport.apiPost(
        service = SERVICE,
        operationId = "issueMqttToken",
        path = "/v1/auth/mqtt-token",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getMyProfile(): UserProfile = transport.apiGet(SERVICE, "getMyProfile", ME_PATH).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun updateMyProfile(request: UpdateProfileRequest): UserProfile =
        transport.apiPut(SERVICE, "updateMyProfile", ME_PATH) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun deleteMyAccount(): DeleteAccountResponse =
        transport.apiDelete(SERVICE, "deleteMyAccount", ME_PATH).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun lookupUserByPhone(phone: PhoneE164): LookupUserResponse =
        transport.apiGet(SERVICE, "lookupUserByPhone", "/v1/users/lookup") {
            parameter("phone", phone)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listSavedAddresses(): SavedAddressListResponse =
        transport.apiGet(SERVICE, "listSavedAddresses", SAVED_ADDRESSES_PATH).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun createSavedAddress(request: SavedAddressInput, idempotencyKey: String?): SavedAddress =
        transport.apiPost(SERVICE, "createSavedAddress", SAVED_ADDRESSES_PATH, idempotencyKey) {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun updateSavedAddress(addressId: Ulid, request: SavedAddressInput): SavedAddress =
        transport.apiPut(SERVICE, "updateSavedAddress", "$SAVED_ADDRESSES_PATH/$addressId") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun deleteSavedAddress(addressId: Ulid) {
        transport.apiDelete(SERVICE, "deleteSavedAddress", "$SAVED_ADDRESSES_PATH/$addressId")
    }

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun setLanguagePreference(request: LanguagePreference): LanguagePreference =
        transport.apiPut(SERVICE, "setLanguagePreference", "/v1/me/prefs/language") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun setDefaultPaymentMethod(
        request: DefaultPaymentMethodPreference,
    ): DefaultPaymentMethodPreference =
        transport.apiPut(SERVICE, "setDefaultPaymentMethod", "/v1/me/prefs/payment-method") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listEmergencyContacts(): EmergencyContactListResponse =
        transport.apiGet(SERVICE, "listEmergencyContacts", EMERGENCY_CONTACTS_PATH).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun createEmergencyContact(
        request: EmergencyContactInput,
        idempotencyKey: String?,
    ): EmergencyContact = transport.apiPost(
        service = SERVICE,
        operationId = "createEmergencyContact",
        path = EMERGENCY_CONTACTS_PATH,
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun updateEmergencyContact(contactId: Ulid, request: EmergencyContactInput): EmergencyContact =
        transport.apiPut(SERVICE, "updateEmergencyContact", "$EMERGENCY_CONTACTS_PATH/$contactId") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun deleteEmergencyContact(contactId: Ulid) {
        transport.apiDelete(SERVICE, "deleteEmergencyContact", "$EMERGENCY_CONTACTS_PATH/$contactId")
    }

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getLoginBootstrap(): LoginBootstrap =
        transport.apiGet(SERVICE, "getLoginBootstrap", "/v1/me/bootstrap").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getMyPermissions(): EffectivePermissions =
        transport.apiGet(SERVICE, "getMyPermissions", "/v1/me/permissions").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun setOperatingCity(request: OperatingCityPreference): OperatingCityPreference =
        transport.apiPut(SERVICE, "setOperatingCity", "/v1/me/prefs/operating-city") {
            jsonBody(request)
        }.decode()

    private companion object {
        val SERVICE = ApiService.IAM
        const val EMERGENCY_CONTACTS_PATH = "/v1/me/emergency-contacts"
        const val ME_PATH = "/v1/users/me"
        const val SAVED_ADDRESSES_PATH = "/v1/me/saved-addresses"
        const val ADMIN_LOGIN_PATH = "/v1/admin/auth/login"
    }
}
