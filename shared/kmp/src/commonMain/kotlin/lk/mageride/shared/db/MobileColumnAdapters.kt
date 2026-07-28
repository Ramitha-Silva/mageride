package lk.mageride.shared.db

import app.cash.sqldelight.ColumnAdapter
import kotlinx.datetime.LocalDate
import kotlin.time.Instant

// The §0.3 PostgreSQL -> SQLite type mapping, as SQLDelight column adapters.
//
// | Server type                    | SQLite  | Convention                                     |
// |--------------------------------|---------|------------------------------------------------|
// | TIMESTAMPTZ                    | INTEGER | epoch MILLISECONDS, UTC                        |
// | business DATE (Asia/Colombo)   | TEXT    | 'YYYY-MM-DD', already in Asia/Colombo          |
// | BOOLEAN                        | INTEGER | 0 / 1  (SQLDelight's SQLite dialect, no adapter)|
// | money *_minor                  | INTEGER | Rs x 100, may be negative (no adapter, Long)   |
// | GEOGRAPHY(POINT)               | 2x REAL | *_lat / *_lng, no PostGIS on device            |
//
// There are exactly two adapters because there are exactly two conventions that need one. Every
// other column is a SQLite primitive, and `INTEGER AS Int` uses SQLDelight's own IntColumnAdapter.

/**
 * `TIMESTAMPTZ` -> `INTEGER`: epoch milliseconds, UTC (§0.3).
 *
 * **Milliseconds, not seconds.** The whole platform's on-device time convention is one number
 * and this is the only place it is applied, so a column that stored seconds would be off by a
 * factor of a thousand everywhere at once rather than in one screen. Display and business-date
 * maths convert to `Asia/Colombo` in Kotlin (`util/BusinessCalendar`, D-38) — never by storing a
 * local time.
 */
public object EpochMillisAdapter : ColumnAdapter<Instant, Long> {
    override fun decode(databaseValue: Long): Instant = Instant.fromEpochMilliseconds(databaseValue)

    override fun encode(value: Instant): Long = value.toEpochMilliseconds()
}

/**
 * Business `DATE` -> `TEXT`: `'YYYY-MM-DD'`, **already in Asia/Colombo** (§0.3, D-38).
 *
 * ISO-8601 text rather than an epoch day so the column sorts and range-scans as itself — the
 * daily-fee and earnings tables are keyed by it and both are read as ranges. The value must be
 * derived through [lk.mageride.shared.util.BusinessCalendar]: a `fee_date` taken from the
 * handset's own zone is wrong for five and a half hours a day.
 */
public object BusinessDateAdapter : ColumnAdapter<LocalDate, String> {
    override fun decode(databaseValue: String): LocalDate = LocalDate.parse(databaseValue)

    override fun encode(value: LocalDate): String = value.toString()
}
