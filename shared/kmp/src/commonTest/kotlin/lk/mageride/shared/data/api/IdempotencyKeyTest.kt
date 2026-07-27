package lk.mageride.shared.data.api

import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The `Idempotency-Key` generator (`_shared.yaml#/components/parameters/IdempotencyKey`).
 *
 * A key that does not satisfy the contract's pattern is `400 idempotency-key-invalid` on every
 * POST in the platform, so the shape is worth asserting rather than eyeballing.
 */
class IdempotencyKeyTest {

    @Test
    fun a_key_matches_the_contracts_pattern() {
        val key = UlidIdempotencyKeyGenerator(Random(1)) { FIXED_MILLIS }.next()

        assertEquals(UlidIdempotencyKeyGenerator.ULID_LENGTH, key.length)
        assertTrue(key.length in MIN_LENGTH..MAX_LENGTH)
        assertTrue(key.all { it in CROCKFORD }, "unexpected characters in $key")
    }

    @Test
    fun the_timestamp_prefix_is_stable_for_the_same_millisecond() {
        // A ULID sorts by mint time, which is what makes a service command log and a support trace
        // agree on the order the client issued its calls.
        val first = UlidIdempotencyKeyGenerator(Random(1)) { FIXED_MILLIS }.next()
        val second = UlidIdempotencyKeyGenerator(Random(2)) { FIXED_MILLIS }.next()

        assertEquals(first.take(TIMESTAMP_CHARS), second.take(TIMESTAMP_CHARS))
    }

    @Test
    fun a_later_millisecond_sorts_after_an_earlier_one() {
        val earlier = UlidIdempotencyKeyGenerator(Random(1)) { FIXED_MILLIS }.next()
        val later = UlidIdempotencyKeyGenerator(Random(1)) { FIXED_MILLIS + 1 }.next()

        assertTrue(earlier < later, "$earlier should sort before $later")
    }

    @Test
    fun two_keys_minted_in_the_same_millisecond_still_differ() {
        val generator = UlidIdempotencyKeyGenerator(Random(3)) { FIXED_MILLIS }

        val keys = List(SAMPLES) { generator.next() }

        assertEquals(SAMPLES, keys.distinct().size)
    }

    @Test
    fun the_default_generator_uses_the_system_clock() {
        val key = UlidIdempotencyKeyGenerator().next()

        assertEquals(UlidIdempotencyKeyGenerator.ULID_LENGTH, key.length)
        assertTrue(key.all { it in CROCKFORD })
    }

    private companion object {
        /** 2026-07-27T04:15:00Z. */
        const val FIXED_MILLIS = 1_785_298_500_000L
        const val MIN_LENGTH = 16
        const val MAX_LENGTH = 128
        const val TIMESTAMP_CHARS = 10
        const val SAMPLES = 500
        const val CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    }
}
