package lk.mageride.shared.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * RFC 7807 Problem Details — the body of **every** 4xx and 5xx MageRide response
 * (`_shared.yaml#/components/schemas/Problem`, served as `application/problem+json`).
 *
 * `type` is `https://mageride.lk/errors/{code}` where `{code}` is a stable kebab key. Branch on
 * [code] / [errorCode], never on [title] or [status] alone: the codes are globally unique across
 * services and a shipped one is never renamed, whereas the title is a developer-facing English
 * string and one status can carry several codes.
 *
 * **`title` and `detail` are never rendered to a user.** They are English and untranslated by
 * design; the app resolves its Si/Ta/En copy from [code] (D-26).
 *
 * @property type Error type URI. See [code] for the key alone.
 * @property title Short English summary for developers.
 * @property status HTTP status, 400–599.
 * @property detail Human-readable explanation of this occurrence. Never contains PII.
 * @property instance The request path this occurred on.
 * @property traceId W3C `traceparent` of the failing request, for support correlation.
 * @property errors Field-level detail, present on `validation-failed`: field name → messages.
 * @property updateUrl Extension carried on `426 upgrade-required` (D-31): store link to update.
 * @property latestVersion Extension carried on `426 upgrade-required`: the newest published build.
 * @property isMandatory Extension carried on `426 upgrade-required`: `true` blocks the client.
 */
@Serializable
public data class ProblemDetails(
    val type: String,
    val title: String,
    val status: Int,
    val detail: String? = null,
    val instance: String? = null,
    val traceId: String? = null,
    val errors: Map<String, List<String>>? = null,
    val updateUrl: String? = null,
    val latestVersion: String? = null,
    val isMandatory: Boolean? = null,
) {
    /**
     * The stable kebab key from [type], e.g. `offer-expired`.
     *
     * Derived rather than deserialised from an enum field on purpose: a service may register a
     * new code at start-up (`MageRideErrors.Register`, C002), and an older build must degrade to
     * "some error with this key" instead of failing to parse the body that explains the failure.
     */
    public val code: String get() = type.substringAfterLast('/')

    /** [code] resolved against the known registry, or `null` when the server is ahead of us. */
    public val errorCode: ErrorCode? get() = ErrorCode.fromWire(code)

    public companion object {
        /** Prefix every `type` URI carries (D3' §0). */
        public const val TYPE_PREFIX: String = "https://mageride.lk/errors/"
    }
}

/**
 * The stable kebab error-code registry (`_shared.yaml#/components/schemas/ErrorCode`).
 *
 * Mirrors `MageRide.Shared.Errors.MageRideErrors` (C002) one-for-one — the kernel is the runtime
 * registry, this enum is the client's shadow of it, and C118 asserts the two agree.
 *
 * Deliberately **not** `@Serializable`: no contract schema carries an `ErrorCode` field, it only
 * ever arrives inside [ProblemDetails.type]. Resolving it through [fromWire] keeps an unknown
 * server-side code a `null` rather than a `SerializationException` — see [ProblemDetails.code].
 *
 * @property wire The kebab key as it appears in the `type` URI.
 */
@Suppress("TooManyFunctions")
public enum class ErrorCode(public val wire: String) {

    // ---- cross-cutting (kernel-owned, C002) --------------------------------------------------
    VALIDATION_FAILED("validation-failed"),
    BAD_REQUEST("bad-request"),
    UNAUTHORIZED("unauthorized"),
    FORBIDDEN("forbidden"),
    NOT_FOUND("not-found"),
    METHOD_NOT_ALLOWED("method-not-allowed"),
    CONFLICT("conflict"),
    PAYLOAD_TOO_LARGE("payload-too-large"),
    UNSUPPORTED_MEDIA_TYPE("unsupported-media-type"),
    INTERNAL_ERROR("internal-error"),
    DEPENDENCY_UNAVAILABLE("dependency-unavailable"),
    SERVICE_UNAVAILABLE("service-unavailable"),
    UPSTREAM_TIMEOUT("upstream-timeout"),

    // ---- idempotency (R-14, R-18) ------------------------------------------------------------
    IDEMPOTENCY_KEY_REQUIRED("idempotency-key-required"),
    IDEMPOTENCY_KEY_INVALID("idempotency-key-invalid"),
    IDEMPOTENCY_KEY_REUSE("idempotency-key-reuse"),
    IDEMPOTENCY_IN_PROGRESS("idempotency-in-progress"),

    // ---- gateway edge (D-30, D-31) -----------------------------------------------------------
    ATTESTATION_FAILED("attestation-failed"),
    UPGRADE_REQUIRED("upgrade-required"),
    RATE_LIMITED("rate-limited"),

    // ---- iam-svc -----------------------------------------------------------------------------
    INVALID_PHONE("invalid-phone"),
    OTP_EXPIRED("otp-expired"),
    INVALID_OTP("invalid-otp"),
    USER_BLOCKED("user-blocked"),
    AUTH_NOT_FOUND("auth-not-found"),
    DEVICE_MISMATCH("device-mismatch"),
    OTP_LOCKED("otp-locked"),
    OTP_RATE_LIMITED("otp-rate-limited"),

    // ---- registry-svc / provisioning-svc -----------------------------------------------------
    INVALID_VEHICLE_TYPE("invalid-vehicle-type"),
    CSV_INVALID("csv-invalid"),
    MODE_NOT_ALLOWED("mode-not-allowed"),
    NOT_OWNER("not-owner"),
    VEHICLE_NOT_APPROVED("vehicle-not-approved"),
    VEHICLE_NOT_FOUND("vehicle-not-found"),
    REGISTRATION_EXISTS("registration-exists"),
    IMEI_DUPLICATE("imei-duplicate"),
    TOO_MANY_ROWS("too-many-rows"),
    BULK_IN_PROGRESS("bulk-in-progress"),

    // ---- trip-state-svc / ride-svc / dispatch-svc --------------------------------------------
    INVALID_FARE_TOKEN("invalid-fare-token"),
    ILLEGAL_TRANSITION("illegal-transition"),
    PAYMENT_METHOD_INVALID("payment-method-invalid"),
    INSUFFICIENT_WALLET("insufficient-wallet"),
    BOOKING_DISABLED("booking-disabled"),
    NOT_ONLINE("not-online"),
    NOT_RIDE_PARTICIPANT("not-ride-participant"),
    ACTIVE_RIDE_EXISTS("active-ride-exists"),
    DRIVER_ALREADY_LIVE("driver-already-live"),
    OFFER_ALREADY_ACCEPTED("offer-already-accepted"),
    RIDE_TERMINAL("ride-terminal"),
    VERSION_CONFLICT("version-conflict"),
    DIRECTIONAL_LIMIT_REACHED("directional-limit-reached"),
    OFFER_EXPIRED("offer-expired"),
    LOC_REQUEST_RATE_LIMITED("loc-request-rate-limited"),

    // ---- fare-svc / wallet-svc / fleet-svc ---------------------------------------------------
    INVALID_AMOUNT("invalid-amount"),
    UNSERVICEABLE_AREA("unserviceable-area"),
    GATEWAY_ERROR("gateway-error"),
    MERCHANT_NOT_ONBOARDED("merchant-not-onboarded"),
    PAYMENT_ALREADY_SETTLED("payment-already-settled"),
    PAYOUT_PROFILE_NOT_VERIFIED("payout-profile-not-verified"),
    ROUTE_UNAVAILABLE("route-unavailable"),

    // ---- safety-svc / public-bff -------------------------------------------------------------
    NO_EMERGENCY_CONTACT("no-emergency-contact"),
    TOKEN_UNKNOWN("token-unknown"),
    TOKEN_EXPIRED_OR_REVOKED("token-expired-or-revoked"),

    // ---- transit-svc GTFS Dataset Manager (AL-54) --------------------------------------------
    FEED_DUPLICATE("feed-duplicate"),
    FEED_NOT_VALIDATED("feed-not-validated"),
    FEED_ALREADY_ACTIVE("feed-already-active"),
    ;

    /** The full `type` URI this code appears under. */
    public val typeUri: String get() = ProblemDetails.TYPE_PREFIX + wire

    public companion object {
        private val BY_WIRE: Map<String, ErrorCode> = entries.associateBy { it.wire }

        /** Resolves a kebab key, or `null` if the server registered a code this build predates. */
        public fun fromWire(wire: String): ErrorCode? = BY_WIRE[wire]
    }
}
