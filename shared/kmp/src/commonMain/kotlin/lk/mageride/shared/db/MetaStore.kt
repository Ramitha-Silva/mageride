package lk.mageride.shared.db

import kotlin.time.Instant

/**
 * The `meta` key-value table — `mobile_db_schema.md` §1.12.
 *
 * One table, four jobs: the monotonic GPS `seq` watermark (§1.5), the server-pushed cadence hint
 * (§7.5.1), the per-entity sync cursors (§4.2) and the D-31 minimum-app-version gate. Keys are
 * spelled once, in [MetaKeys] — two components spelling `gps.seq` differently is a bug that looks
 * like a lost watermark.
 *
 * Blocking, like everything else over a synchronous SQLDelight driver.
 */
public interface MetaStore {

    /** The value for [key], or `null` when absent (or explicitly stored as null). */
    public fun get(key: String): String?

    /** Stores [value] under [key], replacing anything there. */
    public fun put(key: String, value: String?, now: Instant)

    /** Removes [key]. Removing an absent key is not an error. */
    public fun remove(key: String)

    /** Everything, for diagnostics and the wipe path. */
    public fun all(): Map<String, String?>

    /** Empties the table. */
    public fun clear()
}

/** [get] parsed as a `Long`, or `null` when absent or unparsable. */
public fun MetaStore.getLong(key: String): Long? = get(key)?.toLongOrNull()

/** Stores a `Long`. */
public fun MetaStore.putLong(key: String, value: Long, now: Instant) {
    put(key, value.toString(), now)
}

/** [get] parsed as epoch milliseconds, or `null`. */
public fun MetaStore.getInstant(key: String): Instant? = getLong(key)?.let(Instant::fromEpochMilliseconds)

/** Stores an [Instant] as epoch milliseconds, matching the §0.3 column convention. */
public fun MetaStore.putInstant(key: String, value: Instant, now: Instant) {
    putLong(key, value.toEpochMilliseconds(), now)
}

/**
 * Every key `meta` is allowed to carry.
 *
 * §1.12 gives `'gps.seq'`, `'cadence.intervalMs'`, `'sync.cursor.rides'` and `'min_app_version'`
 * as examples; §0.5 adds the schema revision. The two prefixed families are functions rather than
 * constants because they are per-vehicle and per-entity respectively.
 */
public object MetaKeys {

    /**
     * Watermark prefix for [gpsSeq].
     *
     * **Per vehicle, which §1.12's illustrative `'gps.seq'` is not.** §1.5 requires the sequence
     * to be "monotonic per vehicle_id" and `gps_buffer`'s primary key is `(vehicle_id, seq)`, so a
     * driver who switches vehicles mid-shift would otherwise carry one counter across two
     * vehicles — harmless for the second (it starts high) but fatal for the first if it ever comes
     * back, because its counter would have moved on without the server seeing the gap. One key per
     * vehicle is the only spelling that satisfies the sentence the schema actually states.
     */
    public const val GPS_SEQ_PREFIX: String = "gps.seq."

    /** The last cadence hint the server pushed on `veh/{id}/cmd` (ADD §7.5.1). */
    public const val CADENCE_INTERVAL_MS: String = "cadence.intervalMs"

    /** Prefix for [syncCursor]. */
    public const val SYNC_CURSOR_PREFIX: String = "sync.cursor."

    /** D-31's upgrade gate, as the last `/v1/version/check` answered it. */
    public const val MIN_APP_VERSION: String = "min_app_version"

    /** §0.5 — the app schema revision, beside SQLite's own `PRAGMA user_version`. */
    public const val SCHEMA_REV: String = "schema.rev"

    /** The reserved `seq` high-water mark for one vehicle. */
    public fun gpsSeq(vehicleId: String): String = "$GPS_SEQ_PREFIX$vehicleId"

    /** The last-sync cursor for one entity family — `sync.cursor.rides`, `sync.cursor.wallet`. */
    public fun syncCursor(entity: String): String = "$SYNC_CURSOR_PREFIX$entity"
}

/**
 * The §4.2 read-path cursors, as a typed view over [MetaStore].
 *
 * "On foreground / pull-to-refresh / push wake, the app fetches deltas (cursor in
 * `meta.sync.cursor.*`) and upserts projections." The cursor is an opaque server token — C002's
 * `CursorPage.cursor`, base64url and optionally HMAC-signed — so it is stored and echoed, never
 * parsed. A `null` cursor means "no delta known yet: fetch the first page".
 */
public class SyncCursors(private val meta: MetaStore) {

    /** The stored cursor for [entity], or `null` for a full first fetch. */
    public fun cursor(entity: String): String? = meta.get(MetaKeys.syncCursor(entity))

    /** Records the cursor the last delta page returned. A `null` [cursor] clears it. */
    public fun advance(entity: String, cursor: String?, now: Instant) {
        val key = MetaKeys.syncCursor(entity)
        if (cursor == null) meta.remove(key) else meta.put(key, cursor, now)
    }

    /** Forgets every cursor — the next sync re-reads everything. Part of the logout wipe. */
    public fun reset() {
        meta.all().keys.filter { it.startsWith(MetaKeys.SYNC_CURSOR_PREFIX) }.forEach(meta::remove)
    }

    public companion object {
        /** Entity names the four apps sync. Constants so a typo is a compile error, not an empty list. */
        public const val RIDES: String = "rides"

        /** Driver ride history. */
        public const val RIDE_HISTORY: String = "rideHistory"

        /** Wallet ledger projection. */
        public const val WALLET: String = "wallet"

        /** Push inbox. */
        public const val NOTIFICATIONS: String = "notifications"

        /** Trilingual notification templates (D-26). */
        public const val CONTENT_TEMPLATES: String = "contentTemplates"

        /** In-app help. */
        public const val FAQ: String = "faq"

        /** Driver document expiry (E-03). */
        public const val DOCUMENTS: String = "documents"

        /** Passenger saved addresses. */
        public const val SAVED_ADDRESSES: String = "savedAddresses"
    }
}
