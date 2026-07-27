package lk.mageride.shared.data.api

import kotlin.random.Random
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * Mints the `Idempotency-Key` header value required on every POST mutation (D3' §0, R-14/R-18).
 *
 * Injectable so a test can pin the value, and so an app that wants to persist a key across a
 * process death — the strongest form of R-14, where even a crash mid-payment replays rather than
 * re-charges — can supply its own.
 */
public fun interface IdempotencyKeyGenerator {

    /** A new key. Must match `^[A-Za-z0-9_-]{16,128}$` (`_shared.yaml#/parameters/IdempotencyKey`). */
    public fun next(): String
}

/**
 * The default generator: a canonical 26-character ULID.
 *
 * The contract accepts a ULID or a UUID; a ULID is chosen because it is lexicographically
 * ordered by mint time, so a service's command log and a support trace sort into the order the
 * client actually issued the calls.
 *
 * Layout is the ULID spec: 48 bits of millisecond timestamp then 80 bits of randomness, both
 * Crockford base32, most-significant character first.
 *
 * @property random Randomness source. Seed it to make a test deterministic.
 * @property nowMillis Unix-epoch milliseconds.
 */
@OptIn(ExperimentalTime::class)
public class UlidIdempotencyKeyGenerator(
    private val random: Random = Random.Default,
    private val nowMillis: () -> Long = { Clock.System.now().toEpochMilliseconds() },
) : IdempotencyKeyGenerator {

    override fun next(): String {
        val timestamp = nowMillis()
        return buildString(ULID_LENGTH) {
            for (position in TIMESTAMP_CHARS - 1 downTo 0) {
                append(CROCKFORD[((timestamp ushr (position * BITS_PER_CHAR)) and CHAR_MASK).toInt()])
            }
            repeat(RANDOM_CHARS) { append(CROCKFORD[random.nextInt(CROCKFORD.length)]) }
        }
    }

    public companion object {
        /** Crockford base32: the digits, then the letters minus I, L, O and U. */
        private const val CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
        private const val BITS_PER_CHAR = 5
        private const val CHAR_MASK = 0x1FL
        private const val TIMESTAMP_CHARS = 10
        private const val RANDOM_CHARS = 16

        /** A canonical ULID is always this long. */
        public const val ULID_LENGTH: Int = TIMESTAMP_CHARS + RANDOM_CHARS
    }
}
