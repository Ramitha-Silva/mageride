package lk.mageride.shared.domain.auth

import lk.mageride.shared.data.models.AppSurface
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * Everything the session layer needs that only the app knows, plus the timing knobs.
 *
 * [app] is the one field with no sensible default: it is the `app` claim AL-08 scopes a session
 * by, so the Driver App must pass [AppSurface.DRIVER] and the Passenger App
 * [AppSurface.PASSENGER]. Getting it wrong does not fail loudly — it signs the user in as the
 * other surface and revokes the session they actually wanted.
 *
 * @property app Which of the two apps this build is (AL-08, US-1.12).
 * @property accessTokenRefreshSkew How long before the 30-minute access token expires the client
 *   rotates it anyway. ADD §12.1 calls for "proactive refresh"; the skew has to exceed the worst
 *   plausible request latency or a token that was fresh at send time is stale at arrival.
 * @property refreshRetryCooldown After a *transient* refresh failure (offline, 5xx, breaker open),
 *   how long before another proactive attempt. Without it every request in a dead network would
 *   drive its own refresh round trip.
 * @property mqttRenewSkew How long before the MQTT session JWT expires renewal starts (E-02). Ten
 *   minutes is deliberately generous: the driver handset is the one that loses coverage, and a
 *   renewal that fails at T−10 min has time for several retries before the token dies.
 * @property mqttRenewRetryDelay First backoff after a failed MQTT renewal.
 * @property mqttRenewMaxRetryDelay Ceiling for that backoff.
 * @property otpResendCooldown Fallback resend cooldown when the server sends none (D-32 says 60 s).
 * @property storeNamespace Key prefix inside [lk.mageride.shared.platform.SecureStore]. Namespaced
 *   by [app] so a handset running both surfaces keeps two independent sessions.
 */
public data class AuthConfig(
    val app: AppSurface,
    val accessTokenRefreshSkew: Duration = 2.minutes,
    val refreshRetryCooldown: Duration = 30.seconds,
    val mqttRenewSkew: Duration = 10.minutes,
    val mqttRenewRetryDelay: Duration = 30.seconds,
    val mqttRenewMaxRetryDelay: Duration = 5.minutes,
    val otpResendCooldown: Duration = 60.seconds,
    val storeNamespace: String = "lk.mageride.auth",
) {
    init {
        require(accessTokenRefreshSkew > Duration.ZERO) { "accessTokenRefreshSkew must be positive" }
        require(mqttRenewSkew > Duration.ZERO) { "mqttRenewSkew must be positive" }
        require(mqttRenewRetryDelay > Duration.ZERO) { "mqttRenewRetryDelay must be positive" }
        require(mqttRenewMaxRetryDelay >= mqttRenewRetryDelay) { "mqttRenewMaxRetryDelay must not be smaller" }
    }

    /** The [lk.mageride.shared.platform.SecureStore] key [name] lives under for this surface. */
    public fun storeKey(name: String): String = "$storeNamespace.${app.wire}.$name"
}
