package lk.mageride.shared.domain.auth

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.IssueMqttTokenRequest
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/** What an MQTT session token is bound to (E-02): a vehicle, a device, and optionally a ride. */
private data class MqttBinding(val vehicleId: Ulid, val rideId: Ulid?)

/**
 * Acquires and renews the **MQTT session JWT**, which is not the API access token (E-02).
 *
 * E-02 exists because a 30-minute API token expires mid-trip in low coverage and taking position
 * publishing down with it is how a ride goes dark. So the MQTT credential is a separate token with
 * `TTL = max(active ride + 2 h, 4 h)`, bound to `(vehicleId, deviceId, rideId?)`, and this class
 * keeps it alive on its own schedule:
 *
 * - **Renew early.** [AuthConfig.mqttRenewSkew] before expiry, not at it, so a handset in a dead
 *   spot has several attempts left before the token it still holds stops working.
 * - **A failure never costs the token it already has.** Every renewal error — offline, `5xx`,
 *   even `401` — is retried with backoff while the *current* token stays in place and stays
 *   publishable. Only [release], or the session actually ending, clears it.
 * - **A new ride is a new token.** The ride id is part of what extends the TTL past four hours, so
 *   [bind] with a different `rideId` re-issues rather than reusing.
 *
 * C017 owns the MQTT client; this owns the credential it presents.
 *
 * @param api iam-svc client, resolved lazily for the same reason as in [AuthSessionManager].
 * @param sessions Watched so the token dies with the session it belongs to.
 * @param store Where the token is persisted, so a restart does not need a round trip to publish.
 * @param config Renewal skew and backoff.
 * @param clock Wall clock; injectable so a test can drive expiry on virtual time.
 * @param scope Where the renewal loop and the session watcher run. Pass the app's long-lived
 *   scope; [close] stops both without touching the scope itself.
 */
@OptIn(ExperimentalTime::class)
public class MqttSessionTokenManager(
    private val api: () -> IamApi,
    private val sessions: AuthSessionManager,
    private val store: AuthSessionStore,
    private val config: AuthConfig,
    private val clock: () -> Timestamp = { Clock.System.now() },
    scope: CoroutineScope,
) {
    private val gate = Mutex()
    private val mutableToken = MutableStateFlow<MqttSessionToken?>(null)
    private val workScope = scope

    private var binding: MqttBinding? = null
    private var renewal: Job? = null

    private val watcher: Job = scope.launch {
        sessions.state.collect { state ->
            if (state is SessionState.SignedOut) release()
        }
    }

    /**
     * The token to present to EMQX, or `null` when there is none.
     *
     * A [StateFlow] because C017's MQTT client has to reconnect when it rotates: EMQX validates
     * the JWT at CONNECT, so a renewed token only takes effect on the next connection.
     */
    public val token: StateFlow<MqttSessionToken?> = mutableToken.asStateFlow()

    /**
     * Binds to a vehicle (and optionally a ride) and makes sure a live token exists for it.
     *
     * Idempotent: called again with the same pair while the current token is still comfortably
     * valid, it returns that token and issues nothing.
     *
     * @param vehicleId The vehicle whose `veh/{vehicleId}/pos` topic will be published to.
     * @param rideId The active ride, when there is one. Omitting it during a ride yields the
     *   four-hour floor instead of `ride + 2 h`.
     * @return The token now in force.
     */
    public suspend fun bind(vehicleId: Ulid, rideId: Ulid? = null): MqttSessionToken {
        val wanted = MqttBinding(vehicleId, rideId)
        val current = gate.withLock {
            binding = wanted
            val existing = mutableToken.value ?: store.loadMqttToken()?.also { mutableToken.value = it }
            if (existing != null && existing.covers(vehicleId, rideId) && !needsRenewal(existing)) {
                existing
            } else {
                issue(wanted)
            }
        }
        startRenewal()
        return current
    }

    /** Drops the binding and the token — end of ride, going offline, logout, revocation. */
    public suspend fun release() {
        renewal?.cancel()
        renewal = null
        gate.withLock {
            binding = null
            mutableToken.value = null
            store.clearMqttToken()
        }
    }

    /** Stops the renewal loop and the session watcher. The scope passed in is left alone. */
    public fun close() {
        renewal?.cancel()
        renewal = null
        watcher.cancel()
    }

    private fun startRenewal() {
        renewal?.cancel()
        renewal = workScope.launch { renewalLoop() }
    }

    /**
     * Sleeps until the renewal point, renews, repeats.
     *
     * After a failure the token is still the old one, so the next pass computes a negative wait
     * and comes straight back round — which is why the backoff delay is here and not in [renew].
     */
    private suspend fun renewalLoop() {
        var backoff = config.mqttRenewRetryDelay
        while (currentCoroutineContext().isActive) {
            val current = mutableToken.value ?: return
            val wanted = binding ?: return
            val wait = current.expiresAt - clock() - config.mqttRenewSkew
            if (wait > Duration.ZERO) delay(wait)
            if (renew(wanted)) {
                backoff = config.mqttRenewRetryDelay
            } else {
                delay(backoff)
                backoff = (backoff * BACKOFF_FACTOR).coerceAtMost(config.mqttRenewMaxRetryDelay)
            }
        }
    }

    /**
     * One renewal attempt.
     *
     * Catches every [MageRideError], `401` included. A rejected *API* credential is not this
     * token's problem — [AuthSessionManager] is already deciding whether that means "revoked" or
     * "in a tunnel", and if it means revoked the session watcher will [release] this token anyway.
     * Throwing here instead would kill the loop on the first blip.
     */
    private suspend fun renew(wanted: MqttBinding): Boolean = try {
        gate.withLock { if (binding == wanted) issue(wanted) else null } != null
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: MageRideError) {
        false
    }

    /** Mints a token for [wanted] and publishes it. Caller holds [gate]. */
    private suspend fun issue(wanted: MqttBinding): MqttSessionToken {
        val device = store.deviceId()
        val response = api().issueMqttToken(
            IssueMqttTokenRequest(vehicleId = wanted.vehicleId, deviceId = device, rideId = wanted.rideId),
        )
        val issued = MqttSessionToken(
            jwt = response.mqttJwt,
            expiresAt = clock() + response.expiresIn.seconds,
            vehicleId = wanted.vehicleId,
            deviceId = device,
            rideId = wanted.rideId,
        )
        store.saveMqttToken(issued)
        mutableToken.value = issued
        return issued
    }

    private fun needsRenewal(current: MqttSessionToken): Boolean = clock() >= current.expiresAt - config.mqttRenewSkew

    private companion object {
        /** Doubling, the same shape as the HTTP retry policy's backoff (D6' §8.3). */
        const val BACKOFF_FACTOR = 2
    }
}
