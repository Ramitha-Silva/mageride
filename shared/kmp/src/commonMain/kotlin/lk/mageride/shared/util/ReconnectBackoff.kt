package lk.mageride.shared.util

import kotlin.math.pow
import kotlin.random.Random
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * Jittered exponential reconnect backoff — 1 s to 60 s with a **symmetric ±25 % band** (R-09).
 *
 * One rule for both real-time planes: MQTT (D6' §3.5) and the SignalR hub
 * (`realtime/signalr-hub.md` §1.2) specify the same curve, because they fail together. A regional
 * 4G outage ends for every handset in the cell at the same instant, and an unjittered backoff
 * turns that into a synchronised reconnect wave at exactly the moment the broker is least able to
 * absorb one — EMQX's 500-connections/s/listener limit then refuses the vehicles that are trying
 * hardest to come back.
 *
 * The band is symmetric on purpose, and it is *not* clamped back to [max]: the C002 kernel makes
 * the same choice for Polly, and for the same reason — a decorrelated or one-sided curve lets a
 * large fleet re-synchronise into a second thundering herd.
 *
 * Not thread-safe; one instance per connection.
 *
 * @param initial The first delay.
 * @param max The ceiling the *base* delay grows to, before jitter.
 * @param multiplier Growth per attempt.
 * @param jitterFraction Half-width of the band, as a fraction — `0.25` is the spec's ±25 %.
 * @param random Injectable so a test can assert the band rather than sample it.
 */
public class ReconnectBackoff(
    private val initial: Duration = MIN_DELAY,
    private val max: Duration = MAX_DELAY,
    private val multiplier: Double = 2.0,
    private val jitterFraction: Double = JITTER_FRACTION,
    private val random: Random = Random.Default,
) {
    private var attempts = 0

    /** How many delays have been handed out since the last [reset]. */
    public val attempt: Int get() = attempts

    /** The next delay to wait before reconnecting. */
    public fun next(): Duration {
        val base = (initial * multiplier.pow(attempts)).coerceAtMost(max)
        attempts++
        val factor = 1.0 + random.nextDouble(-jitterFraction, jitterFraction)
        return base * factor
    }

    /** Call on a successful connect — the next failure starts from [initial] again. */
    public fun reset() {
        attempts = 0
    }

    public companion object {
        /** First delay after a drop (D6' §3.5, signalr-hub.md §1.2). */
        public val MIN_DELAY: Duration = 1.seconds

        /** Ceiling of the base delay. */
        public val MAX_DELAY: Duration = 60.seconds

        /** ±25 %. */
        public const val JITTER_FRACTION: Double = 0.25
    }
}
