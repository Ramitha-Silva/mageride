import Foundation
import MageRideShared

/// Where SCR-PI-019's stars go, and the one place this component could not finish.
///
/// **There is no contract to POST a Mode C ride rating.** `ride.yaml` declares no rating operation at
/// all; its only mention of the word is `RideDriver.rating`, which is a *read*. The platform's only
/// rating routes are trip-state-svc's `/v1/sessions/{sessionId}/rating` and `…/driver-rating` — and
/// **calling either with a ride id would cross the R-01 boundary the root `CLAUDE.md` forbids in as
/// many words**: ride-svc owns Mode C, trip-state-svc owns Mode A/B, and a `sessionId` path parameter
/// is not a place to put a `rideId`.
///
/// C074 found the same gap from the driver's side (*"no route writes a `subject_kind='ride'` rating
/// although the column, query-svc's read and D3' §Part 3 all expect one"*) and C080 from the
/// passenger's. Three components have now hit it from three directions, which should settle whether
/// it is real.
///
/// **So the rating is captured and queued, not dropped.** `mobile_db_schema.md` §1.11's
/// `ratings_pending` exists for exactly this, and its `subject_kind` column already distinguishes
/// `'ride'` from `'session'` — the schema anticipating the route nobody wrote. What this must **not**
/// do is report a success it did not achieve, which is why SCR-PI-019's CTA says *Save rating* rather
/// than *Submit*.
///
/// A protocol because ``PassengerDatabase`` opens a real protected SQLite file, and because the
/// interesting assertion — *"the stars were queued against this ride and this driver"* — is about
/// what was handed over.
protocol RideRatings: AnyObject {

    /// Records that `rideId` has been rated, and who was rated.
    ///
    /// ⚠ **The stars, the chips and the comment are not stored**, because §1.11 has no columns for
    /// them: it was designed as a prompt queue rather than a draft store. That is the honest shape of
    /// the gap — the app remembers *that* this ride is rated and by whom, and the content is lost if
    /// the process dies before a route exists to take it. Adding the route should add the columns.
    func queue(rideId: String, driverId: String?) async

    /// Whether this handset has already rated `rideId` — what stops SCR-PI-018 offering it twice.
    func isRated(rideId: String) async -> Bool
}

/// ``RideRatings`` over §1.11, through `:shared`'s own door.
///
/// An `actor` for ``LocalRecentPlaces``' reason and one more: every call underneath is **blocking**
/// (SQLDelight's Native driver is synchronous), and the caller is a view model on the main actor.
///
/// The SQL is `IosRatingsPendingKt`'s rather than this file's, and the reason is the bridge —
/// `created_at` is a `kotlin.time.Instant` and Swift may only build one through `IosInstant.kt`'s
/// door, and the two spellings the row's CHECK constraints fix (`'ride'`, `'passenger_to_driver'`)
/// belong beside the schema that has to accept them. C096 made the same call for §2.2 and C093 for
/// the driver's §1.6.
actor LocalRideRatings: RideRatings {

    private let databases: PassengerDatabase
    private let now: () -> Date

    init(databases: PassengerDatabase, now: @escaping () -> Date = Date.init) {
        self.databases = databases
        self.now = now
    }

    /// A database that will not open loses the queue entry rather than the rating screen.
    ///
    /// There is nothing a passenger can do about a protected file, and — because there is no route to
    /// send this to anyway — the row's only reader today is ``isRated(rideId:)``. Failing the screen
    /// on it would refuse a rating over a queue nobody drains yet.
    func queue(rideId: String, driverId: String?) async {
        guard let database = await databases.get() else { return }
        IosRatingsPendingKt.queueRideRating(
            database: database,
            rideId: rideId,
            driverId: driverId ?? "",
            nowMillis: Int64(now().timeIntervalSince1970 * 1000)
        )
    }

    func isRated(rideId: String) async -> Bool {
        guard let database = await databases.get() else { return false }
        return IosRatingsPendingKt.isRideRatingQueued(database: database, rideId: rideId)
    }
}
