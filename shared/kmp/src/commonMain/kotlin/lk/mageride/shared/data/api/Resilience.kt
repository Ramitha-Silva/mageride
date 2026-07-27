package lk.mageride.shared.data.api

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlin.math.min
import kotlin.random.Random
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * Retry, backoff and jitter, exactly as D6' §8.3 specifies them for the client side.
 *
 * > *"transient REST/Kafka — 3 attempts, exponential 100 ms→2 s, ±25% jitter; idempotent only
 * > (Idempotency-Key on mutations)."*
 *
 * "Idempotent only" is what makes a POST retryable here rather than forbidden: every POST
 * mutation carries an `Idempotency-Key`, and a duplicate replays the original response from the
 * service command log (R-14, R-18). A POST *without* a key — the six HMAC-signed provider
 * callbacks — is never retried, because deduping it is the gateway's job, not ours.
 *
 * @property maxAttempts Total sends, not extra ones: 3 means one try and two retries.
 * @property initialBackoff Delay before the second attempt.
 * @property maxBackoff Ceiling on the exponential growth.
 * @property jitterFraction Fraction of the delay applied as symmetric random spread, ±25%,
 *   which is what stops a fleet of reconnecting drivers retrying in lockstep (R-09).
 * @property respectRetryAfter Whether a `429`/`503` `Retry-After` overrides the computed delay.
 */
public data class RetryPolicy(
    val maxAttempts: Int = 3,
    val initialBackoff: Duration = 100.milliseconds,
    val maxBackoff: Duration = 2.seconds,
    val jitterFraction: Double = 0.25,
    val respectRetryAfter: Boolean = true,
) {
    init {
        require(maxAttempts >= 1) { "maxAttempts must be at least 1" }
        require(jitterFraction in 0.0..1.0) { "jitterFraction must be between 0 and 1" }
    }

    /**
     * The wait before attempt [attempt], where attempt 1 has already happened.
     *
     * @param attempt 1-based index of the attempt that just failed.
     * @param retryAfterSeconds The response's `Retry-After`, if it sent one.
     * @param random Jitter source.
     */
    public fun backoffFor(attempt: Int, retryAfterSeconds: Int?, random: Random): Duration {
        if (respectRetryAfter && retryAfterSeconds != null) return retryAfterSeconds.seconds
        val exponential = initialBackoff.inWholeMilliseconds shl (attempt - 1).coerceAtMost(MAX_SHIFT)
        val capped = min(exponential, maxBackoff.inWholeMilliseconds)
        val spread = (capped * jitterFraction).toLong()
        val jittered = if (spread <= 0L) capped else capped - spread + random.nextLong(2 * spread + 1)
        return jittered.coerceAtLeast(0L).milliseconds
    }

    private companion object {
        /** 100 ms shifted 20 times is already ~29 hours; the cap makes the shift total nonsense-proof. */
        const val MAX_SHIFT = 20
    }
}

/**
 * Circuit-breaker thresholds, per D6' §8.3.
 *
 * > *"per external dependency — open after 5 failures/30 s, half-open probe after 15 s."*
 *
 * @property failureThreshold Failures inside [samplingWindow] that trip the breaker.
 * @property samplingWindow How far back a failure counts.
 * @property openDuration How long the breaker rejects calls before admitting one probe.
 */
public data class CircuitBreakerPolicy(
    val failureThreshold: Int = 5,
    val samplingWindow: Duration = 30.seconds,
    val openDuration: Duration = 15.seconds,
) {
    init {
        require(failureThreshold >= 1) { "failureThreshold must be at least 1" }
    }
}

/**
 * One breaker per [ApiService], so a single sick service cannot take the app offline.
 *
 * Only *server-side* failures count — 5xx, network and timeout. A `400`, a `409` or a `429` is
 * the service working correctly and saying no, and counting those would open the breaker in
 * front of a healthy dependency the moment a user mistyped an OTP.
 *
 * @param policy Thresholds, from [ApiConfig].
 * @param nowMillis Monotonic-enough clock, injected so a test does not have to sleep 15 s.
 */
public class CircuitBreaker(private val policy: CircuitBreakerPolicy, private val nowMillis: () -> Long) {
    private val guard = Mutex()
    private val states = mutableMapOf<ApiService, ServiceState>()

    /**
     * Admits a call, or refuses it.
     *
     * @throws MageRideError.CircuitOpen while the breaker is open, and for every call but the
     *   one probe once the cooldown has elapsed.
     */
    public suspend fun onCallStarted(service: ApiService) {
        guard.withLock {
            val state = states.getOrPut(service) { ServiceState() }
            val now = nowMillis()
            when (state.phase) {
                Phase.CLOSED -> Unit

                Phase.PROBING -> throw MageRideError.CircuitOpen(service, policy.openDuration.inWholeMilliseconds)

                Phase.OPEN ->
                    if (now < state.openUntil) {
                        throw MageRideError.CircuitOpen(service, state.openUntil - now)
                    } else {
                        state.phase = Phase.PROBING
                    }
            }
        }
    }

    /**
     * Records the outcome of a call that [onCallStarted] admitted.
     *
     * A failed probe re-opens the breaker on its own — the half-open state exists to ask "is it
     * back?", so one "no" is a complete answer and must not wait for another four.
     */
    public suspend fun onCallFinished(service: ApiService, failed: Boolean) {
        guard.withLock {
            val state = states.getOrPut(service) { ServiceState() }
            val now = nowMillis()
            val wasProbing = state.phase == Phase.PROBING
            if (!failed) {
                state.failures.clear()
                state.openUntil = 0L
                state.phase = Phase.CLOSED
                return@withLock
            }
            if (wasProbing) {
                trip(state, now)
                return@withLock
            }
            state.failures.removeAll { it < now - policy.samplingWindow.inWholeMilliseconds }
            state.failures.add(now)
            if (state.failures.size >= policy.failureThreshold) trip(state, now)
        }
    }

    /** Whether [service] is refusing calls right now. Diagnostics only — the pipeline uses the pair above. */
    public suspend fun isOpen(service: ApiService): Boolean = guard.withLock {
        val state = states[service] ?: return@withLock false
        state.phase != Phase.CLOSED && nowMillis() < state.openUntil
    }

    private fun trip(state: ServiceState, now: Long) {
        state.phase = Phase.OPEN
        state.openUntil = now + policy.openDuration.inWholeMilliseconds
        state.failures.clear()
    }

    private enum class Phase { CLOSED, OPEN, PROBING }

    private class ServiceState {
        val failures: MutableList<Long> = mutableListOf()
        var openUntil: Long = 0L
        var phase: Phase = Phase.CLOSED
    }
}
