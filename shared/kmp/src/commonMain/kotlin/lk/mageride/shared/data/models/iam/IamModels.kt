package lk.mageride.shared.data.models.iam

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.FleetRole
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Role
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// iam-svc — auth, profile, session, saved addresses.
// Source: backend/contracts/iam.yaml (D3' "iam-svc — auth, profile, token").
//
// Sign-in is by surface (AL-07): the passenger and driver apps are Phone OTP only; the portals
// use password / Google / Apple. There is NO MFA step anywhere — AL-37 removed it, and the
// TOTP enrolment pair and POST /v1/admin/auth/mfa/verify are deleted, not deprecated.
//
// Tokens are RS256 access (30 min) plus an opaque single-use rotating refresh token (D-29), with
// ONE ACTIVE DEVICE PER `app` CLAIM (AL-08) — see AppSurface.

/**
 * An issued session (`_shared.yaml#/components/schemas/TokenPair`, D-29).
 *
 * Only iam-svc mints these. The refresh token is opaque and **single-use**: a successful refresh
 * rotates the `jti`, and presenting a spent token revokes the whole session family.
 *
 * C014 owns where these are stored (`SecureStore`) and when they are rotated; C012 owns only the
 * shape.
 *
 * @property accessToken RS256 JWT, 30-minute lifetime, JWKS-verifiable.
 * @property refreshToken Opaque rotating refresh token.
 * @property expiresIn Access-token lifetime in seconds. The contract declares it `const: 1800`.
 */
@Serializable
public data class TokenPair(val accessToken: String, val refreshToken: String, val expiresIn: Int) {
    public companion object {
        /** The access-token lifetime the contract pins (`const: 1800`). */
        public const val ACCESS_TOKEN_LIFETIME_SECONDS: Int = 1800
    }
}

/**
 * The payment method a passenger's profile defaults to (`iam.users.default_payment_method` CHECK).
 *
 * Narrower than either ride-side method enum: it is a *preference*, so it carries neither `cod`
 * (package-only, chosen per booking) nor `scan_driver_qr` (a settlement-time choice).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class DefaultPaymentMethod(public val wire: String) {
    @SerialName("cash")
    CASH("cash"),

    @SerialName("lankaqr")
    LANKAQR("lankaqr"),

    @SerialName("onepay")
    ONEPAY("onepay"),
}

/**
 * The signed-in user (`iam.yaml#/components/schemas/UserProfile`).
 *
 * [role] is the primary role; [roles] is every granted one and **effective permissions are their
 * union** (AL-06). A client that keys its navigation off [role] alone will hide a screen from a
 * user who legitimately has two roles.
 *
 * @property userId The user's id.
 * @property phone E.164 mobile. App users sign in with this; portal identities also carry [email].
 * @property email Portal identities only.
 * @property firstName Display name, at most 120 characters.
 * @property photoUrl Profile photo URL.
 * @property role Primary role.
 * @property roles Every granted role; permissions are the union (AL-06).
 * @property fleetRole Org-scoped sub-role, when the user belongs to a fleet (AL-03).
 * @property language UI language (D-26).
 * @property operatingCityCode Chosen on the first-run city screen; keys `config.operating_cities`.
 * @property defaultPaymentMethod Preferred ride payment method.
 * @property notifPrefs Per-notification-type switches; safety-critical types cannot be muted.
 * @property createdAt When the account was created.
 */
@Serializable
public data class UserProfile(
    val userId: Ulid,
    val phone: PhoneE164,
    val email: String? = null,
    val firstName: String? = null,
    val photoUrl: String? = null,
    val role: Role,
    val roles: List<Role>? = null,
    val fleetRole: FleetRole? = null,
    val language: Language? = null,
    val operatingCityCode: String? = null,
    val defaultPaymentMethod: DefaultPaymentMethod? = null,
    val notifPrefs: Map<String, Boolean>? = null,
    val createdAt: Timestamp? = null,
) {
    /** Every role this user actually holds, whether or not the server sent the `roles` array. */
    public val effectiveRoles: Set<Role> get() = (roles ?: emptyList()).toSet() + role
}

// ---------------------------------------------------------------------------------------------
// Auth — phone OTP (apps)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/auth/otp/request` — start login and send an OTP over SMS.
 *
 * Apps only. Rate limited by a Redis token bucket: 60-second resend, 5 per hour (D-32).
 *
 * @property phone The number to send the OTP to.
 * @property deviceId Stable per-install identifier; binds the session (AL-08).
 * @property fcmToken Push token, so the very first notification can be delivered.
 * @property role Which app is signing in. Portals do not use this endpoint.
 */
@Serializable
public data class RequestOtpRequest(
    val phone: PhoneE164,
    val deviceId: String,
    val fcmToken: String? = null,
    val role: AppSurface? = null,
)

/**
 * `POST /v1/auth/otp/request` — 200.
 *
 * @property authId Handle for this attempt; echoed back on verify and resend.
 * @property attemptsRemaining OTP entries left before `423 otp-locked`.
 * @property cooldownSeconds Seconds until a resend is allowed. The contract defaults it to 60.
 * @property isBlocked Whether the number is blocked outright (`user-blocked`).
 */
@Serializable
public data class RequestOtpResponse(
    val authId: Ulid,
    val attemptsRemaining: Int,
    val cooldownSeconds: Int,
    val isBlocked: Boolean,
)

/**
 * `POST /v1/auth/otp/verify`.
 *
 * @property authId From [RequestOtpResponse].
 * @property otp Six digits.
 * @property deviceId Must match the `deviceId` sent to `/v1/auth/otp/request`, or
 *   `409 device-mismatch`.
 */
@Serializable
public data class VerifyOtpRequest(val authId: Ulid, val otp: String, val deviceId: String)

/**
 * `POST /v1/auth/otp/verify` — 200.
 *
 * On the wire this is `allOf(TokenPair, { user, isNewUser })`, i.e. one flat JSON object. It is
 * flattened here because that is what the object is — [tokens] recomposes the pair for callers
 * that only want the session.
 *
 * @property isNewUser `true` when this verify created the account. Drives first-run onboarding.
 */
@Serializable
public data class VerifyOtpResponse(
    val accessToken: String,
    val refreshToken: String,
    val expiresIn: Int,
    val user: UserProfile,
    val isNewUser: Boolean,
) {
    /** The issued session, as the shared [TokenPair] C014 stores. */
    public val tokens: TokenPair
        get() = TokenPair(accessToken = accessToken, refreshToken = refreshToken, expiresIn = expiresIn)
}

/**
 * `POST /v1/auth/otp/resend`.
 *
 * @property authId The in-flight attempt to re-send for.
 */
@Serializable
public data class ResendOtpRequest(val authId: Ulid)

/**
 * `POST /v1/auth/otp/resend` — 200.
 *
 * Counts against the same 5-per-hour budget as the original request (D-32).
 *
 * @property attemptsRemaining OTP entries left.
 * @property cooldownSeconds Seconds until another resend is allowed.
 */
@Serializable
public data class ResendOtpResponse(val attemptsRemaining: Int, val cooldownSeconds: Int)

// ---------------------------------------------------------------------------------------------
// Auth — token lifecycle and portal sign-in
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/auth/refresh`. Responds with a rotated [TokenPair].
 *
 * @property refreshToken The single-use token to rotate.
 */
@Serializable
public data class RefreshSessionRequest(val refreshToken: String)

/**
 * `POST /v1/auth/password` and one arm of `POST /v1/admin/auth/login`
 * (`iam.yaml#/components/schemas/PasswordLogin`).
 *
 * **No MFA challenge follows** (AL-37). Failed attempts count toward the lock-out that replaced
 * the second factor, which is what `423 otp-locked` means on this route.
 *
 * @property email Portal identity.
 * @property password At least 12 characters.
 */
@Serializable
public data class PasswordLogin(val email: String, val password: String)

/**
 * `POST /v1/auth/google` and `POST /v1/auth/apple`.
 *
 * **Portals only** (AL-07) — the gateway rejects these routes for an app `X-Platform`.
 *
 * @property idToken The provider's OIDC ID token, verified against the provider's JWKS.
 */
@Serializable
public data class IdTokenLogin(val idToken: String)

/**
 * The Google-OIDC arm of `POST /v1/admin/auth/login`.
 *
 * The request body is `oneOf(PasswordLogin, this)`. Modelled as two types rather than one
 * all-nullable class so a caller cannot send half of each — the union lives in C013's two
 * overloads, not in a shape that admits a body the server would reject.
 *
 * @property googleAuthCode Authorization code from the Google OIDC redirect.
 * @property redirectUri The redirect URI the code was issued for.
 */
@Serializable
public data class GoogleAuthCodeLogin(val googleAuthCode: String, val redirectUri: String? = null)

/**
 * The 200 of every non-OTP sign-in — Google, Apple, password, and the Admin Portal login.
 *
 * On the wire this is `allOf(TokenPair, { user })`; see [VerifyOtpResponse] for why it is flat.
 */
@Serializable
public data class AuthSessionResponse(
    val accessToken: String,
    val refreshToken: String,
    val expiresIn: Int,
    val user: UserProfile,
) {
    /** The issued session, as the shared [TokenPair] C014 stores. */
    public val tokens: TokenPair
        get() = TokenPair(accessToken = accessToken, refreshToken = refreshToken, expiresIn = expiresIn)
}

/**
 * `POST /v1/auth/mqtt-token`.
 *
 * @property vehicleId The vehicle the token authorises publishing for.
 * @property deviceId The publishing device.
 * @property rideId Binds the token to a ride, which is what extends its TTL past four hours.
 */
@Serializable
public data class IssueMqttTokenRequest(val vehicleId: Ulid, val deviceId: String, val rideId: Ulid? = null)

/**
 * `POST /v1/auth/mqtt-token` — 200.
 *
 * The MQTT session JWT is **decoupled from the API access token** (E-02, D-21): its TTL is
 * `max(active ride + 2 h, 4 h)` and it is bound to `(vehicleId, deviceId, rideId?)`, so a mid-trip
 * API refresh that fails in poor coverage does not stop position publishing.
 *
 * @property mqttJwt The token EMQX validates against its cached JWKS.
 * @property expiresIn Seconds; never less than [MIN_LIFETIME_SECONDS].
 */
@Serializable
public data class IssueMqttTokenResponse(val mqttJwt: String, val expiresIn: Int) {
    public companion object {
        /** Four hours — the floor the contract states for an MQTT session token. */
        public const val MIN_LIFETIME_SECONDS: Int = 14400
    }
}

// ---------------------------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------------------------

/**
 * `PUT /v1/users/me` (US-1.5). Every field is optional; omitted fields are left alone.
 *
 * @property firstName Display name, at most 120 characters.
 * @property photoUrl Profile photo URL.
 * @property language Drives every server-rendered string (D-26).
 * @property notifPrefs Per-notification-type switches.
 */
@Serializable
public data class UpdateProfileRequest(
    val firstName: String? = null,
    val photoUrl: String? = null,
    val language: Language? = null,
    val notifPrefs: Map<String, Boolean>? = null,
)

/**
 * `DELETE /v1/users/me` — 202.
 *
 * Accepted, not immediate: the request becomes a pdpa-svc erasure request and may be held by a
 * statutory hold (US-1.8, E-06). Poll `GET /v1/pdpa/{requestId}`.
 *
 * @property requestId The PDPA request to poll.
 */
@Serializable
public data class DeleteAccountResponse(val requestId: Ulid)

/**
 * `GET /v1/users/lookup` — 200. Internal, mTLS only.
 *
 * Backs the proxy-booking rider lookup (P-03): ride-svc uses the answer to choose between an
 * in-app FCM location request and the SMS `pickup_confirm` web path. Returns no PII beyond the
 * answer itself.
 *
 * @property registered Whether the number belongs to a registered user.
 * @property userId Present only when [registered].
 */
@Serializable
public data class LookupUserResponse(val registered: Boolean, val userId: Ulid? = null)

/**
 * `PUT /v1/me/prefs/language` — the request body and the 200 are the same shape.
 *
 * Writes `iam.users.language`; there is no `iam.user_prefs` table despite ADD §9.1 naming one
 * (C003 note (d)).
 *
 * @property language The chosen UI language.
 */
@Serializable
public data class LanguagePreference(val language: Language)

/**
 * `PUT /v1/me/prefs/operating-city` — the request body and the 200 are the same shape.
 *
 * AL-27 / US-1.3a. The code chosen on the first-run language/city screen (SCR-DA/DI-002); it keys
 * `config.operating_cities` and seeds the map centroid. An unknown or deactivated code is
 * `400 validation-failed` — a city the platform does not operate in is a client working from a
 * stale list, not a preference.
 *
 * @property operatingCityCode The chosen city's stable machine key, e.g. `colombo`.
 */
@Serializable
public data class OperatingCityPreference(val operatingCityCode: String)

// ---------------------------------------------------------------------------------------------
// Saved addresses (AL-26, US-22.x)
// ---------------------------------------------------------------------------------------------

/**
 * Body of `POST` / `PUT /v1/me/saved-addresses[/{addressId}]`
 * (`iam.yaml#/components/schemas/SavedAddressInput`).
 *
 * **At most one Home and one Work per user** — the C003 partial unique indexes reject a second,
 * and moving the flag on an edit clears it from whichever address held it.
 *
 * @property label Free-form name, at most 60 characters. Passenger-supplied, not platform copy.
 * @property line1 First address line, at most 200 characters.
 * @property line2 Second address line.
 * @property line3 Third address line.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property isHome Marks this as the Home shortcut.
 * @property isWork Marks this as the Work shortcut.
 */
@Serializable
public data class SavedAddressInput(
    val label: String,
    val line1: String,
    val line2: String? = null,
    val line3: String? = null,
    val lat: Double,
    val lng: Double,
    val isHome: Boolean? = null,
    val isWork: Boolean? = null,
)

/**
 * A stored address (`iam.yaml#/components/schemas/SavedAddress`).
 *
 * On the wire this is `allOf({ addressId }, SavedAddressInput)` — flattened here, as everywhere
 * else an `allOf` composes one JSON object.
 *
 * @property addressId The stored row's id.
 */
@Serializable
public data class SavedAddress(
    val addressId: Ulid,
    val label: String,
    val line1: String,
    val line2: String? = null,
    val line3: String? = null,
    val lat: Double,
    val lng: Double,
    val isHome: Boolean? = null,
    val isWork: Boolean? = null,
)

/**
 * `GET /v1/me/saved-addresses` — 200. Home and Work first.
 *
 * Not cursor-paged: a user's saved addresses are a short list by construction.
 *
 * @property items The caller's saved addresses.
 */
@Serializable
public data class SavedAddressListResponse(val items: List<SavedAddress> = emptyList())
