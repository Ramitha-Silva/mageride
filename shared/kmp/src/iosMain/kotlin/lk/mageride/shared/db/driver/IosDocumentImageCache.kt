package lk.mageride.shared.db.driver

import kotlin.time.Duration.Companion.milliseconds

/**
 * Builds a [DocumentImageCache] from a plain millisecond retention (Δ MCS-28).
 *
 * **The constructor takes a `Duration`, which Swift cannot make.** `kotlin.time.Duration` is an
 * inline value class and the Objective-C export flattens it to an opaque `Long` whose encoding is a
 * packed nanos/millis pair with a tag bit — not a millisecond count. A Swift call site passing
 * `2_592_000_000` would compile and mean something entirely different, which is the worst of the
 * three possible outcomes.
 *
 * The same reason `IosAppConfig` exists rather than letting Swift build an `ApiConfig`, and the same
 * reason `timestampFromEpochMillis` exists rather than letting it reach `Instant.Companion`.
 */
public fun documentImageCacheOf(db: DriverDb, retentionMillis: Long): DocumentImageCache =
    DocumentImageCache(db, retentionMillis.milliseconds)
