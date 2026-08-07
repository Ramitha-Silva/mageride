// Named after the operations rather than after the table, as `IosNotificationInbox.kt` is: somebody
// looking for "where does iOS read the recents" looks for the read, not for a row type.

package lk.mageride.shared.db

import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.math.round
import kotlin.time.Instant

// ### Why this file exists at all
//
// The same reason `IosNotificationInbox.kt` gives for §1.6: **the generated SQLDelight query types do
// not belong on the bridge.** `PassengerDb.sql.placeRecentsQueries.selectRecent(…)` answers an
// `app.cash.sqldelight.Query<Place_recents>`, and `Query` lives in the SQLDelight runtime — a
// dependency the framework does not `export` — so a Swift caller would be reaching through an
// implicitly-exported generic from another module over a generated row class. `last_used_at` is a
// `kotlin.time.Instant` on top of that, and Swift may only build one through `IosInstant.kt`'s door.
//
// There is a second reason, and it is the one [rememberRecentPlace] is built around: the row id is a
// **derived** value, and deriving it is what makes `INSERT OR REPLACE` mean anything. Putting that
// rule on the far side of the bridge would put a table's primary key in an app.
//
// Both functions speak in [GeocodedPlace], which is what `GET /v1/geo/search` answers with and what
// every destination hand-off in the passenger app carries. §2.2 is *"recent / searched locations"* —
// the same thing in a different store — so there is no second row type to convert through.

// Top-level functions taking the database as a parameter rather than extensions on [PassengerDb],
// which is the shape every other `iosMain` helper in this module has. A Kotlin extension's exported
// Swift spelling — category method or file-class static — is a compiler detail, and this host cannot
// link a framework to find out which one it chose.

/**
 * The most recently chosen places, newest first — SCR-PI-010's *"Recent"* rows and SCR-PI-008's
 * empty state.
 *
 * Every row comes back marked [GeocodedPlaceSource.RECENT], which is what draws the 🕘 beside it
 * where a geocoded row draws a 📍. §2.2 stores one address line and no city, so `city` is null.
 *
 * @param database The open passenger database.
 * @param limit How many rows to read.
 */
public fun readRecentPlaces(database: PassengerDb, limit: Int): List<GeocodedPlace> =
    database.sql.placeRecentsQueries.selectRecent(limit.toLong()).executeAsList().map { row ->
        GeocodedPlace(
            lat = row.lat,
            lng = row.lng,
            displayName = row.label,
            line1 = row.line1,
            city = null,
            source = GeocodedPlaceSource.RECENT,
        )
    }

/**
 * Records that [place] was chosen, or bumps it if this handset has chosen it before.
 *
 * **The id is the coordinate, to five decimals** — about a metre, which is finer than any geocoder
 * answers and coarser than the float noise two lookups of the same place produce. Nominatim has no
 * stable id across queries, so the position is the only identity a place actually has, and without a
 * derived one `INSERT OR REPLACE` would fill the list with the same address.
 *
 * **`touch` first, then `insert`.** `INSERT OR REPLACE` deletes the old row, which would reset
 * `use_count` to 1 — the field `selectFrequent` orders by. Both statements plus §4.3's cap run in one
 * transaction, so a crash between them cannot leave the table over its cap with the new row missing.
 *
 * Mirrors `apps/passenger-android/.../home/RecentPlaces.kt`'s `LocalRecentPlaces.remember`, which
 * holds the same two rules in the *app* module. That copy should move here — see the C096 handoff.
 *
 * @param database The open passenger database.
 * @param place What the passenger chose.
 * @param nowMillis Now, epoch milliseconds (§0.3).
 */
public fun rememberRecentPlace(database: PassengerDb, place: GeocodedPlace, nowMillis: Long) {
    val id = "${roundTo5(place.lat)},${roundTo5(place.lng)}"
    val now = Instant.fromEpochMilliseconds(nowMillis)
    val queries = database.sql.placeRecentsQueries

    database.transaction {
        val known = queries.selectRecent(Long.MAX_VALUE).executeAsList().any { it.id == id }
        if (known) {
            queries.touch(last_used_at = now, id = id)
        } else {
            queries.insert(
                id = id,
                label = place.displayName,
                line1 = place.line1,
                lat = place.lat,
                lng = place.lng,
                use_count = 1,
                last_used_at = now,
            )
        }
        // §4.3's cap. The index is on `last_used_at DESC`, so the sweep runs in the same order the
        // sheet reads in and a place that falls off is one nobody has used in a long time.
        queries.deleteBeyondCap(RECENT_PLACES_CAP)
    }
}

/** How many rows §2.2 keeps. It is a UX cache, not a history. */
private const val RECENT_PLACES_CAP = 50L

/** Five decimal places — about a metre. */
private const val PRECISION = 100_000.0

private fun roundTo5(value: Double): Long = round(value * PRECISION).toLong()
