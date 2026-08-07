// Named after the operation rather than after the table, as `IosPlaceRecents.kt` and
// `IosNotificationInbox.kt` are: somebody looking for "where does iOS queue a rating" looks for the
// write, not for a row type.

package lk.mageride.shared.db

import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.time.Instant

// ### Why this file exists at all
//
// The same reason `IosPlaceRecents.kt` gives for §2.2: **`created_at` is a `kotlin.time.Instant` and
// Swift may only build one through `IosInstant.kt`'s door**, so a Swift call site into
// `sql.ratingsPendingQueries.upsert(…)` would either have to import that helper and pass the result
// through a generated signature, or guess at the exported spelling of a stdlib companion. Putting the
// write here means the app hands over milliseconds and the two spellings that the row's CHECK
// constraints fix — `'ride'` and `'passenger_to_driver'` — are written **once**, next to the schema
// they have to satisfy, rather than at a call site where a typo is a runtime constraint violation.
//
// ### What this write does NOT store, and why that matters
//
// **The stars, the chips and the comment have no columns.** §1.11 was designed as a *prompt queue* —
// "which completed rides still owe a rating" — not as a draft store, and its whole row is
// `(subject_id, subject_kind, ratee_id, direction, prompt_shown, created_at)`.
//
// That is the honest shape of a **contract gap**, not a design choice: `ride.yaml` declares no rating
// operation at all, and trip-state-svc's `/v1/sessions/{sessionId}/rating` is scoped to a *session* —
// calling it with a ride id would cross the R-01 boundary the root `CLAUDE.md` forbids in as many
// words. C074 found the same hole from the driver's side and C080 from the passenger's. Until a route
// exists, an app can record **that** a ride was rated and by whom; the content is lost if the process
// dies first, and the screen must therefore say the rating was *saved* rather than *sent*. Adding the
// route should add the columns.

/**
 * Queues a passenger's rating of their driver for a **Mode C ride** (`mobile_db_schema.md` §1.11).
 *
 * `INSERT OR REPLACE`, so rating the same ride twice leaves one row rather than failing on the
 * primary key — a passenger who re-opens SCR-PI-019 and changes their mind has changed their mind,
 * not created a second rating.
 *
 * `prompt_shown` is `true` because by the time this is called the passenger has been *shown* the
 * screen and answered it; the flag exists so a later sweep does not prompt again for a ride that has
 * already been asked about.
 *
 * @param database The open passenger database.
 * @param rideId The ride being rated — §1.11's `subject_id`, with `subject_kind = 'ride'`.
 * @param driverId Who is being rated. Empty when the ride read that would have named them failed;
 *   the row is still worth keeping, because the ride id is what a later sync resolves the driver
 *   from.
 * @param nowMillis Now, epoch milliseconds (§0.3).
 */
public fun queueRideRating(database: PassengerDb, rideId: String, driverId: String, nowMillis: Long) {
    database.sql.ratingsPendingQueries.upsert(
        subject_id = rideId,
        subject_kind = SUBJECT_KIND_RIDE,
        ratee_id = driverId,
        direction = DIRECTION_PASSENGER_TO_DRIVER,
        prompt_shown = true,
        created_at = Instant.fromEpochMilliseconds(nowMillis),
    )
}

/** Whether [rideId] has already been rated on this handset — what stops SCR-PI-018 offering it twice. */
public fun isRideRatingQueued(database: PassengerDb, rideId: String): Boolean =
    database.sql.ratingsPendingQueries.selectAll().executeAsList().any { it.subject_id == rideId }

/**
 * §1.11's `subject_kind` for a Mode C ride.
 *
 * The column's own CHECK is `IN ('ride','session')`, and the split is R-01's: ride-svc owns Mode C
 * and trip-state-svc owns Mode A/B. The schema anticipating both is the schema anticipating the route
 * nobody wrote.
 */
private const val SUBJECT_KIND_RIDE = "ride"

/** §1.11's `direction` for the passenger rating their driver (US-18.1). */
private const val DIRECTION_PASSENGER_TO_DRIVER = "passenger_to_driver"
