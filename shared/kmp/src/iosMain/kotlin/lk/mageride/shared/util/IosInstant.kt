package lk.mageride.shared.util

import lk.mageride.shared.data.models.Timestamp

// Reading and writing a Timestamp from Swift (C088).
//
// WHY THESE EXIST. `Timestamp` is `kotlin.time.Instant`, and Swift can already *read* one —
// `toEpochMilliseconds()` crosses the bridge and C086's OTP countdown uses it. What it cannot do is
// **make** one: `Instant.Companion.parse` takes a defaulted `DateTimeFormat` and
// `fromEpochMilliseconds` sits on a stdlib companion whose exported spelling is a property of the
// compiler rather than of this codebase. A Swift call site that guessed either would compile on one
// Kotlin version and not the next.
//
// There is a second reason, and it is the one that matters more: `parse` is the same parser the rest
// of the platform uses. SCR-DI-014's fifteen seconds are measured from `expiresAt` on a `ride_offer`
// push, and reading that string with `ISO8601DateFormatter` on one platform and `Instant.parse` on
// the other is two answers to "when does this offer die" — the one thing an atomic single-winner
// accept (R-02) cannot afford to disagree about.

/**
 * The instant [text] denotes, or `null` for a value this build cannot read.
 *
 * Never throws: the string arrived over the network on a push envelope, and an offer whose deadline
 * cannot be parsed still has a local TTL to fall back on. Losing the ring is better than losing the
 * ride.
 */
public fun parseTimestampOrNull(text: String): Timestamp? = runCatching { Timestamp.parse(text) }.getOrNull()

/** [millis] since the epoch, as a [Timestamp]. */
public fun timestampFromEpochMillis(millis: Long): Timestamp = Timestamp.fromEpochMilliseconds(millis)

/** [instant] as milliseconds since the epoch — the reading half, so no call site spells it twice. */
public fun timestampEpochMillis(instant: Timestamp): Long = instant.toEpochMilliseconds()
