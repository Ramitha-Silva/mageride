import Foundation
import MageRideShared

/// SCR-PI-010's *"Recent"* rows, and the only door onto `mobile_db_schema.md` §2.2's
/// `place_recents`.
///
/// §2.2 calls the table *"recent / searched locations"* and marks it **local-only** — no `dirty`
/// column, no `synced_at`, no outbox partner — so this is a device's memory of where its owner has
/// been looking, and it never leaves the handset. That is also why it is a table rather than a `GET`:
/// the platform is not told.
///
/// **The row is written when a destination is chosen on SCR-PI-008**, not when a ride is booked. A
/// passenger who searched for somewhere and then changed their mind still searched for it, and the
/// §2.2 column is `use_count`, not `trip_count`. Booking writes nothing here.
///
/// A protocol because ``PassengerDatabase`` opens a real protected SQLite file — a model test needs
/// to hand over rows rather than open one.
protocol RecentPlaces: AnyObject {

    /// The most recently used places, newest first.
    func recent(limit: Int) async -> [GeocodedPlace]

    /// Records that [place] was chosen, or bumps it if it is already there.
    func remember(_ place: GeocodedPlace) async
}

extension RecentPlaces {

    /// How many rows SCR-PI-010's sheet asks for.
    ///
    /// The wireframe draws one, above a full-bleed map — more than a handful would turn the home
    /// screen into a list of places with a map behind it. Same number as the Android twin's
    /// `RecentPlaces.DEFAULT_LIMIT`.
    static var defaultLimit: Int { 3 }

    /// [recent(limit:)] at ``defaultLimit``. An extension rather than a default argument, which a
    /// protocol requirement cannot carry.
    func recent() async -> [GeocodedPlace] {
        await recent(limit: Self.defaultLimit)
    }
}

/// ``RecentPlaces`` over §2.2, through `:shared`'s own door.
///
/// An `actor` for ``PassengerDatabase``'s reason and one more: every call underneath is **blocking**
/// (SQLDelight's Native driver is synchronous), and both callers are view models on the main actor.
/// An actor is where that work goes off it.
///
/// The SQL is `IosPlaceRecentsKt`'s rather than this file's, and the reason is the bridge — the
/// generated `Query<Place_recents>` comes from a dependency the framework does not `export`, and
/// `last_used_at` is a `kotlin.time.Instant`. See that file's own note; C093 made the same call for
/// the driver's §1.6 inbox.
actor LocalRecentPlaces: RecentPlaces {

    private let databases: PassengerDatabase
    private let now: () -> Date

    init(databases: PassengerDatabase, now: @escaping () -> Date = Date.init) {
        self.databases = databases
        self.now = now
    }

    /// A database that will not open answers *no rows*, not an error. The search bar above them is
    /// the way to a destination regardless, and there is nothing a passenger can do about a
    /// protected file.
    func recent(limit: Int) async -> [GeocodedPlace] {
        guard let database = await databases.get() else { return [] }
        return IosPlaceRecentsKt.readRecentPlaces(database: database, limit: Int32(limit))
    }

    func remember(_ place: GeocodedPlace) async {
        guard let database = await databases.get() else { return }
        IosPlaceRecentsKt.rememberRecentPlace(
            database: database,
            place: place,
            nowMillis: Int64(now().timeIntervalSince1970 * 1000)
        )
    }
}
