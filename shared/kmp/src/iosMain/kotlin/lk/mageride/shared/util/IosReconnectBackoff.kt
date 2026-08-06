package lk.mageride.shared.util

/**
 * [ReconnectBackoff]'s schedule, in seconds.
 *
 * **Why a wrapper at all.** `ReconnectBackoff.next()` answers a `kotlin.time.Duration`, which is an
 * inline value class — the Objective-C export flattens it to an opaque `Long` whose encoding is the
 * standard library's private business, so a Swift caller cannot turn one into a sleep. This exposes
 * the same schedule as the seconds Swift's `Task.sleep` and `DispatchQueue.asyncAfter` take.
 *
 * The schedule itself is not restated here and must not be: R-09 bounds how hard a city's worth of
 * handsets may retry after a broker restart — a fixed one-second retry from every driver in Colombo
 * *is* the reconnect storm — so the jitter, the ceiling and the growth all stay in the shared class
 * that both platforms use.
 */
public class IosReconnectBackoff {

    private val backoff = ReconnectBackoff()

    /** How many delays have been handed out. */
    public val attempt: Int get() = backoff.attempt

    /** The next delay, in seconds. */
    public fun nextSeconds(): Double = backoff.next().inWholeMilliseconds / MILLIS_PER_SECOND

    /** Back to the start — called on a successful CONNECT. */
    public fun reset() {
        backoff.reset()
    }

    private companion object {
        const val MILLIS_PER_SECOND = 1000.0
    }
}
