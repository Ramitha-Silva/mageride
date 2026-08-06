package lk.mageride.passenger.home

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import lk.mageride.passenger.di.PassengerDatabase
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.db.passenger.Place_recents
import kotlin.time.Clock
import kotlin.time.Instant

/**
 * SCR-PA-010's *"Recent"* rows, and the only writer of `place_recents`.
 *
 * `mobile_db_schema.md` §2.2 calls the table *"recent / searched locations"* and marks it
 * **local-only** — no `dirty` column, no `synced_at`, no outbox partner — so this is a device's
 * memory of where its owner has been looking, and it never leaves the handset. That is also why it
 * is a table rather than a `GET`: the platform is not told.
 *
 * **The row is written when a destination is chosen on SCR-PA-008**, not when a ride is booked. A
 * passenger who searched for somewhere and then changed their mind still searched for it, and the
 * §2.2 column is `use_count`, not `trip_count`. Booking writes nothing here; C079 reads the same
 * seam for its own recents.
 *
 * An interface because [PassengerDatabase] opens a real SQLCipher file through the Android driver,
 * which on this build host is a stub whose every member throws — a view-model test needs to hand
 * over rows rather than open a database.
 */
internal interface RecentPlaces {

    /** The most recently used places, newest first. */
    suspend fun recent(limit: Int = DEFAULT_LIMIT): List<GeocodedPlace>

    /** Records that [place] was chosen, or bumps it if it already exists. */
    suspend fun remember(place: GeocodedPlace)

    companion object {
        /**
         * How many rows SCR-PA-010's sheet asks for.
         *
         * The wireframe draws one and the sheet is a peek height above a full-bleed map; more than
         * a handful would turn the home screen into a list of places with a map behind it.
         */
        const val DEFAULT_LIMIT = 3
    }
}

/** [RecentPlaces] over §2.2's `place_recents`. */
internal class LocalRecentPlaces(
    private val databases: PassengerDatabase,
    private val clock: () -> Instant = { Clock.System.now() },
) : RecentPlaces {

    override suspend fun recent(limit: Int): List<GeocodedPlace> = withContext(Dispatchers.IO) {
        databases.get().sql.placeRecentsQueries
            .selectRecent(limit.toLong())
            .executeAsList()
            .map(Place_recents::toPlace)
    }

    /**
     * Writes the row, then trims the table.
     *
     * `INSERT OR REPLACE` on a **coordinate-derived id** rather than a random one, so choosing the
     * same place twice bumps one row instead of filling the list with the same address: the id is
     * what makes "or replace" mean anything. `touch` first, so a repeat keeps its `use_count` —
     * `INSERT OR REPLACE` deletes the old row and would reset the count to 1, which is the field
     * `selectFrequent` exists to order by.
     */
    override suspend fun remember(place: GeocodedPlace): Unit = withContext(Dispatchers.IO) {
        val database = databases.get()
        val queries = database.sql.placeRecentsQueries
        val id = idOf(place)
        val now = clock()

        database.transaction {
            if (queries.selectRecent(Long.MAX_VALUE).executeAsList().any { it.id == id }) {
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
            // §4.3's cap. The index is on `last_used_at DESC`, so the sweep is the same order the
            // sheet reads in and a place that fell off is one nobody has used in a long time.
            queries.deleteBeyondCap(CAP)
        }
    }

    private companion object {

        /** How many rows the table keeps. §2.2 is a UX cache, not a history. */
        const val CAP = 50L

        /**
         * The row id — the place's coordinate, to five decimals.
         *
         * About a metre, which is finer than any geocoder answers and coarser than the float noise
         * two lookups of the same place produce. Nominatim has no stable id across queries, so the
         * position is the only identity a place actually has.
         */
        fun idOf(place: GeocodedPlace): String = "${roundTo5(place.lat)},${roundTo5(place.lng)}"

        fun roundTo5(value: Double): Long = kotlin.math.round(value * PRECISION).toLong()

        const val PRECISION = 100_000.0
    }
}

/** A stored row as the search and the home sheet both draw it — a ★/🕘 place among the 📍 ones. */
private fun Place_recents.toPlace(): GeocodedPlace = GeocodedPlace(
    lat = lat,
    lng = lng,
    displayName = label,
    line1 = line1,
    source = GeocodedPlaceSource.RECENT,
)
