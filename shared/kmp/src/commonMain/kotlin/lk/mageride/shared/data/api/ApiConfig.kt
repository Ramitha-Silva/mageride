package lk.mageride.shared.data.api

import lk.mageride.shared.data.models.ClientPlatform
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * How much of a call the client writes to its log.
 *
 * Deliberately our own enum rather than Ktor's: this is configuration the four apps set, two of
 * them from Swift, and it should not change shape when the HTTP engine does.
 */
public enum class ApiLogLevel {
    /** Log nothing. The production default. */
    NONE,

    /** Method, URL and status. */
    INFO,

    /** Adds headers — with `Authorization` and `X-Attestation` redacted. */
    HEADERS,

    /** Adds bodies. **Never ship this**: request bodies carry OTPs, phone numbers and tokens. */
    BODY,
}

/**
 * Per-call deadlines, from D6' §8.3 "Timeouts".
 *
 * > *"API 15 s … Per-service `connectTimeout` set."*
 *
 * The 90-second payment-provider and 30-second OCR budgets in the same section are the
 * *server's* deadlines against OnePay and Gemini; from the app every route is behind the
 * gateway and gets the API budget. [paymentRequestTimeout] exists because a redirect-issuing
 * payment initiation is the one app-facing call that legitimately waits on the provider.
 *
 * @property requestTimeout Whole-call deadline for an ordinary route.
 * @property paymentRequestTimeout Whole-call deadline for a payment initiation or top-up.
 * @property connectTimeout TCP/TLS connect deadline.
 * @property socketTimeout Idle deadline between two reads.
 */
public data class ApiTimeouts(
    val requestTimeout: Duration = 15.seconds,
    val paymentRequestTimeout: Duration = 90.seconds,
    val connectTimeout: Duration = 10.seconds,
    val socketTimeout: Duration = 15.seconds,
)

/**
 * Everything the HTTP layer needs that this module cannot know: which gateway, which build,
 * which platform.
 *
 * [appVersion] and [platform] are not diagnostics — they are the D-31 min-version gate's inputs.
 * The gateway reads `X-App-Version` and `X-Platform` on every app-originated request and answers
 * `426 upgrade-required` below the floor, so a wrong value here breaks every call, not one.
 *
 * @property baseUrl Gateway origin, e.g. `https://api.mageride.lk`. A trailing `/` is trimmed;
 *   contract paths are absolute (`/v1/...`) and are appended verbatim.
 * @property appVersion This build's semantic version, matching `^\d+\.\d+\.\d+([-+]…)?$`.
 * @property platform `android` or `ios` (D-31).
 * @property timeouts Per-call deadlines (D6' §8.3).
 * @property retry Retry, backoff and jitter (D6' §8.3).
 * @property circuitBreaker Per-service breaker thresholds (D6' §8.3).
 * @property logLevel How much of a call to log. [ApiLogLevel.NONE] in release builds.
 * @property userAgent Sent as `User-Agent`; helps a gateway log tell two app surfaces apart.
 */
public data class ApiConfig(
    val baseUrl: String,
    val appVersion: String,
    val platform: ClientPlatform,
    val timeouts: ApiTimeouts = ApiTimeouts(),
    val retry: RetryPolicy = RetryPolicy(),
    val circuitBreaker: CircuitBreakerPolicy = CircuitBreakerPolicy(),
    val logLevel: ApiLogLevel = ApiLogLevel.NONE,
    val userAgent: String? = null,
) {
    init {
        require(baseUrl.isNotBlank()) { "baseUrl must not be blank" }
        require(appVersion.isNotBlank()) { "appVersion must not be blank" }
    }

    /** [baseUrl] without its trailing slash, so `origin + "/v1/rides"` is always well formed. */
    public val origin: String get() = baseUrl.trimEnd('/')

    /** The absolute URL of a contract path such as `/v1/rides/history`. */
    public fun urlFor(path: String): String = origin + path
}
