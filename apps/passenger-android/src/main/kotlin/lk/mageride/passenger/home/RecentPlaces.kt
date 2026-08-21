package lk.mageride.passenger.home

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import lk.mageride.passenger.di.PassengerDatabase
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.Language
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
 * **A stored label is a snapshot of a language, and D-26 says it should not be.** The row keeps the
 * text the geocoder answered with at the moment the place was chosen, so a passenger who switches
 * to සිංහල finds their recent destinations still in English — nothing about a `TEXT` column
 * re-translates itself. Two things now keep the list in the language in force:
 *
 * * **Choosing a place again rewrites its label**, because `remember` is a fresh geocode of the
 *   same coordinate and `touch` writes what came back rather than only bumping the counters.
 * * **A language change sweeps the table**, re-labelling every row through `GET /v1/geo/reverse`
 *   the next time the list is read. See [recent].
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

/**
 * [RecentPlaces] over §2.2's `place_recents`.
 *
 * @param query Only for the language sweep in [recent] — `GET /v1/geo/reverse` is the one way to
 *   ask what a coordinate is called in a language, and a stored row is only a coordinate and a
 *   label. Injected as `QueryApi`, so it is the `LocalisedQueryApi` from the graph and the sweep
 *   does not have to say which language it wants.
 * @param preferences Holds the whole-table language stamp. See `AppPreferences.recentsLanguage`
 *   for why one stamp is enough and no column is needed.
 */
internal class LocalRecentPlaces(
    private val databases: PassengerDatabase,
    private val query: QueryApi,
    private val preferences: AppPreferences,
    private val clock: () -> Instant = { Clock.System.now() },
) : RecentPlaces {

    /**
     * Serialises the sweep. This is a Koin `single`, and SCR-PA-010's sheet and SCR-PA-008's
     * defaults can both ask for recents inside the same second — without this, a language change
     * starts two sweeps of the same rows and pays the geocoder twice for them.
     */
    private val sweep = Mutex()

    /**
     * The stored rows, after the table has been brought into the language in force.
     *
     * The sweep is **guarded by a stamp and so runs once per language change**, not once per read:
     * on every other call this is the same local query it always was. It is synchronous rather
     * than fired into a background scope because both callers already load this off the main
     * thread and tolerate a failure — and a list that silently rewrites itself a second after the
     * screen drew it is worse than one that takes a moment to appear.
     *
     * A row the geocoder could not answer for keeps its old label and the stamp is **not** moved,
     * so an offline passenger gets their list unchanged and the sweep is retried rather than
     * skipped. Nothing here fails the read: recents are a convenience, and no destination is
     * unreachable because its label is in the wrong script.
     */
    override suspend fun recent(limit: Int): List<GeocodedPlace> = withContext(Dispatchers.IO) {
        relabelForCurrentLanguage()

        databases.get().sql.placeRecentsQueries
            .selectRecent(limit.toLong())
            .executeAsList()
            .map(Place_recents::toPlace)
    }

    /**
     * Re-reads every row's coordinate in [AppPreferences.language] and writes the answer back.
     *
     * The whole table rather than the handful on screen, because the stamp it moves stands for all
     * of it — and §4.3's cap keeps "all of it" at [CAP]. Sequential: this is a self-hosted
     * Nominatim (D-14) shared with every other passenger in the country, and query-svc caches by
     * coordinate **and** language, so the second sweep of the same list costs it nothing.
     */
    private suspend fun relabelForCurrentLanguage() = sweep.withLock {
        val language = preferences.language ?: return@withLock
        // Re-read inside the lock: the sweep this call queued behind may already have done it.
        if (preferences.recentsLanguage == language) return@withLock

        val queries = databases.get().sql.placeRecentsQueries
        val rows = queries.selectRecent(CAP).executeAsList()
        var complete = true

        for (row in rows) {
            val place = reverseOrNull(row.lat, row.lng)

            if (place == null) {
                complete = false
                continue
            }

            queries.relabel(label = place.displayName, line1 = place.line1, id = row.id)
        }

        if (complete) {
            preferences.recentsLanguage = language
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun reverseOrNull(lat: Double, lng: Double): GeocodedPlace? = try {
        query.reverseGeocode(lat = lat, lng = lng)
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        // Offline, or a geocoder with nothing at that coordinate. Either way the row keeps the
        // label it has, which is a real place name in some language rather than a blank row.
        null
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
                // The label goes in too: [place] is a fresh geocode of the same coordinate, so it
                // is already in the language in force. A `touch` that moved only the counters is
                // what used to pin a row to whichever language the passenger first found it in.
                queries.touch(last_used_at = now, label = place.displayName, line1 = place.line1, id = id)
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

        // An empty table is trivially in whatever language is in force, and so is a table whose
        // only rows were written by this method. Claiming the stamp on the FIRST write is what
        // stops a passenger who has just chosen their first destination paying for a sweep of a
        // list that is already correct.
        preferences.language?.let { language ->
            if (preferences.recentsLanguage == null) preferences.recentsLanguage = language
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
