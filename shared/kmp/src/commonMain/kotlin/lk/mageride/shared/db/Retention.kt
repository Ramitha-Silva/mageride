package lk.mageride.shared.db

import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import kotlin.time.Duration
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Instant

// mobile_db_schema.md §4.3 — retention / eviction.
//
// | Table                | Policy                                                              |
// |----------------------|---------------------------------------------------------------------|
// | gps_buffer           | ring: delete ACKED at once; cap the backlog by age and by row count |
// | command_outbox       | delete ACKED after 24 h; keep FAILED until user-dismissed           |
// | notifications        | keep 30 days or last 200, whichever first                          |
// | rides / ride_history | keep last 90 days or 100 rows; full history is server-paged        |
// | fare_estimates       | delete on expires_at                                                |
// | dispatch_offers      | delete on expires_at + small grace                                  |
// | proof_upload_queue   | delete UPLOADED after the server confirms; keep FAILED              |
// | offline_map_bundles  | evict STALE/expired; respect a total on-disk size budget            |
// | all caches           | full wipe on logout, device-revoke (AL-08) or PDPA erasure (E-06)   |

/** A table the sweep touches. The name is the SQLite table, used for row counting. */
public enum class RetentionTable(public val tableName: String) {
    /** §1.5, both apps. */
    GPS_BUFFER("gps_buffer"),

    /** §1.4, both apps. */
    COMMAND_OUTBOX("command_outbox"),

    /** §1.6, both apps. */
    NOTIFICATIONS("notifications"),

    /** §2.3, passenger. */
    RIDES("rides"),

    /** §3.5, driver. */
    RIDE_HISTORY("ride_history"),

    /** §2.4, passenger. */
    FARE_ESTIMATES("fare_estimates"),

    /** §3.3, driver. */
    DISPATCH_OFFERS("dispatch_offers"),

    /** §3.6, driver. */
    PROOF_UPLOAD_QUEUE("proof_upload_queue"),

    /** §2.7, passenger — a C018 addition, see [RetentionPolicy]. */
    TRIP_SHARES("trip_shares"),

    /** §2.2, passenger — a C018 addition, see [RetentionPolicy]. */
    PLACE_RECENTS("place_recents"),

    /** §3.13, driver — a C018 addition, see [RetentionPolicy]. */
    JOB_BOARD("job_board"),
}

/**
 * The §4.3 numbers.
 *
 * Everything with a figure in §4.3 carries it here. Three properties are **C018 additions** where
 * §4.3 is silent but the table plainly grows without bound, and each is named as such:
 * [tripShareGrace], [placeRecentsMax] and [jobBoardGrace]. `location_requests`, the cache tables
 * (`vehicles`, `documents`, `wallet`, `driver_level`, …) and the singleton rows are deliberately
 * NOT swept — they are bounded by the account itself, and dropping them would blank a screen the
 * user can only refill by going online.
 *
 * @property outboxAcked §4.3 — `ACKED` command rows are kept this long.
 * @property notifications §4.3 — "keep 30 days …".
 * @property notificationsMax §4.3 — "… or last 200, whichever first".
 * @property rides §4.3 — "keep last 90 days …" for both `rides` and `ride_history`.
 * @property ridesMax §4.3 — "… or 100 rows".
 * @property offerGrace §4.3 — "delete on `expires_at` + small grace" for `dispatch_offers`. The
 *   grace exists so the sheet can still render "this offer expired" for a moment after it did.
 * @property gps The ring bounds, shared with [GpsBuffer].
 * @property tripShareGrace **C018.** How long a revoked or expired share token is kept so the
 *   share sheet can say it lapsed (D-34). §4.3 has no row for `trip_shares`.
 * @property placeRecentsMax **C018.** `place_recents` is local-only UX with no server copy and no
 *   rule in §4.3; unbounded it grows once per search forever.
 * @property jobBoardGrace **C018.** Scheduled rides whose pickup time has passed are no longer
 *   claimable (US-6A.4); §4.3 has no row for `job_board`.
 */
public data class RetentionPolicy(
    val outboxAcked: Duration = 24.hours,
    val notifications: Duration = 30.days,
    val notificationsMax: Long = 200,
    val rides: Duration = 90.days,
    val ridesMax: Long = 100,
    val offerGrace: Duration = 5.minutes,
    val gps: GpsRetentionPolicy = GpsRetentionPolicy(),
    val tripShareGrace: Duration = 1.days,
    val placeRecentsMax: Long = 50,
    val jobBoardGrace: Duration = 6.hours,
)

/**
 * What one sweep removed.
 *
 * @property sweptAt The instant the sweep ran with.
 * @property removed Rows removed, per table. A table the app does not own is simply absent.
 * @property mapBundlesToRelease Bundles §4.3 says to evict — **their rows are still in the
 *   database**. Dropping the row first would orphan the PMTiles file on disk with nothing left
 *   pointing at it, so the caller deletes the file at each `local_path` and then calls
 *   `offlineMapBundlesQueries.delete(id)`. Only the app can touch the filesystem.
 */
public data class RetentionReport(
    val sweptAt: Instant,
    val removed: Map<RetentionTable, Long>,
    val mapBundlesToRelease: List<MapBundleRelease> = emptyList(),
) {
    /** Rows removed in total. */
    public val total: Long get() = removed.values.sum()
}

/**
 * A downloaded map bundle the sweep wants gone (MAP-09).
 *
 * @property id `offline_map_bundles.id`, for the follow-up delete.
 * @property localPath The file to remove; `null` when the download never completed.
 * @property sizeBytes What removing it will free, as recorded at download time.
 */
public data class MapBundleRelease(val id: String, val localPath: String?, val sizeBytes: Long?)

/** Runs §4.3 for one app's tables. Implemented per database — the table sets differ. */
public interface Retention {

    /** Applies every rule and reports what went. Blocking; run it off the main thread. */
    public fun sweep(now: Instant, policy: RetentionPolicy = RetentionPolicy()): RetentionReport
}

/**
 * `SELECT COUNT(*)` for one table, straight off the driver.
 *
 * Retention needs a before/after count for eleven tables across two databases; adding a `count:`
 * query to eleven `.sq` files (twice over, once per generated package) to get the same number is
 * more surface than this is worth. The table name comes from [RetentionTable] — an enum this
 * module owns — so no caller-supplied string ever reaches the statement.
 */
internal fun SqlDriver.countRows(table: RetentionTable): Long = executeQuery(
    identifier = null,
    sql = "SELECT COUNT(*) FROM ${table.tableName}",
    mapper = { cursor ->
        val count = if (cursor.next().value) cursor.getLong(0) ?: 0L else 0L
        QueryResult.Value(count)
    },
    parameters = 0,
).value

/**
 * Runs one §4.3 rule and records how many rows it removed.
 *
 * Counting either side of the statement rather than reading an affected-row count keeps the
 * report honest when a rule is expressed as two statements (an age cut and a row cap, say) and
 * costs one cheap `COUNT(*)` per rule on tables that are bounded by construction.
 */
internal inline fun MutableMap<RetentionTable, Long>.prune(
    driver: SqlDriver,
    table: RetentionTable,
    block: () -> Unit,
) {
    val before = driver.countRows(table)
    block()
    val after = driver.countRows(table)
    this[table] = (this[table] ?: 0L) + (before - after).coerceAtLeast(0)
}

/**
 * Every user table in the open database — read from `sqlite_master`, so it needs no per-app list.
 *
 * SQLite's own `sqlite_%` tables and AGP's `android_metadata` are excluded; both are engine
 * bookkeeping and neither belongs to the app.
 */
internal fun SqlDriver.userTables(): List<String> = executeQuery(
    identifier = null,
    sql = "SELECT name FROM sqlite_master WHERE type = 'table' " +
        "AND name NOT LIKE 'sqlite_%' AND name <> 'android_metadata' ORDER BY name",
    mapper = { cursor ->
        val names = mutableListOf<String>()
        while (cursor.next().value) {
            cursor.getString(0)?.let(names::add)
        }
        QueryResult.Value(names.toList())
    },
    parameters = 0,
).value

/**
 * Empties every table in the open database.
 *
 * The §0.4 wipe is "wipe the whole DB file" — [DatabaseDriverFactory.delete] does that and is what
 * logout, `403 device-revoked` (AL-08) and PDPA erasure (E-06) should call, because an emptied
 * SQLite file still holds the old pages until something overwrites them. This is the in-place
 * fallback for a caller that cannot close the database first, and it is driven off `sqlite_master`
 * so it cannot miss a table someone added to one schema and not the other.
 */
internal fun SqlDriver.deleteEveryRow() {
    userTables().forEach { table ->
        execute(identifier = null, sql = "DELETE FROM $table", parameters = 0)
    }
}
