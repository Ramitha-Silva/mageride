package lk.mageride.shared.db

import lk.mageride.shared.data.models.AppSurface

/**
 * Which of the two on-device databases an install owns.
 *
 * `mobile_db_schema.md` §0.2 is unambiguous: **one database file per app**, and the two schemas
 * do not merge even when a single handset runs both the passenger and the driver app — AL-08's
 * single-active-device rule is per app, and so is everything cached under it. That is why
 * `:shared` builds two SQLDelight databases rather than one with a superset of tables:
 * `MageRidePassengerDatabase.Schema.create()` physically cannot produce a `dispatch_offers`
 * table, and `MageRideDriverDatabase.Schema.create()` cannot produce a `saved_addresses` one.
 *
 * @property surface The `app` claim the session is scoped by (C014, AL-08). The `auth_session`
 *   row records the same value, so a database opened for the wrong surface is detectable.
 * @property databaseName The file name from §0.2. Passed to the platform driver factory; on
 *   Android it is a name under the app's private databases directory, on iOS a file in
 *   Application Support.
 */
public enum class MageRideApp(public val surface: AppSurface, public val databaseName: String) {
    /** `mageride_passenger.db` — §1 shared tables + §2 passenger tables. */
    PASSENGER(AppSurface.PASSENGER, "mageride_passenger.db"),

    /** `mageride_driver.db` — §1 shared tables + §3 driver tables. */
    DRIVER(AppSurface.DRIVER, "mageride_driver.db"),
    ;

    public companion object {
        /** The app surface as its wire spelling, or `null` when it names neither. */
        public fun fromWire(wire: String): MageRideApp? = entries.firstOrNull { it.surface.wire == wire }
    }
}
