import Foundation
import MageRideShared

/// SCR-DI-030's data — the driver's own trips, one trip's detail, and US-18.2's rating.
///
/// ### The list is query-svc's, not ride-svc's
///
/// `GET /v1/trips/{userId}` is the read model, it spans **both planes** — its SQL selects rides on
/// `accepted_driver_id = @UserId` and sessions on `driver_id = @UserId` — and it is implemented.
/// `GET /v1/rides/history` looks like the obvious alternative and is not: it is Mode C only, its row
/// carries a `driver` block that exists so a *passenger* can call the driver back (AL-36), and ride-svc
/// leaves it unmapped on purpose (its own CLAUDE.md files it under C048). A driver's history is every
/// journey they drove, which is what the first one answers.
///
/// ### `rating` is *this caller's* rating, which is exactly the question the screen asks
///
/// query-svc's trip-detail SQL joins `trips.ratings` on `subject_id = r.id AND rater_id = @UserId`. So
/// for a driver, ``detail(driverId:tripId:)``'s `rating` means **"the stars I left on this trip"** — not
/// the passenger's rating of them — and a non-`nil` one is what turns the wireframe's *"Rate ★"* link
/// into *"rated ★5"*. `TripSummary` carries no rating and no distance, which is why the screen reads a
/// detail per row; see ``RideHistoryModel``.
///
/// ### The rating write has one door, and it is session-shaped
///
/// **C074's headline spec gap, unchanged.** `trips.ratings.subject_kind` is
/// `CHECK (… IN ('session','ride'))`, query-svc reads ride-subject ratings back, D5' §4.1 says ratings
/// run *"passenger↔driver both directions"* and D3' §Part 3 files US-18.2 against *"trip-state
/// `/sessions/{id}/driver-rating`; **ride**"* — but `ride.yaml` declares **no rating route at all**. The
/// only operation on the platform that writes a driver-to-passenger rating is trip-state-svc's, and its
/// path takes a `sessionId`. So ``ratePassenger(subjectId:passengerId:stars:comment:)`` sends the
/// subject id it is given to the one route that exists: correct for a Mode A/B session, and refused for
/// a Mode C ride until ride-svc gains the route. Wired rather than omitted, on the same reasoning C090
/// wired `cancelScheduled` — the refusal is the honest answer to the deliverable, and a screen that
/// silently dropped the tap would hide it.
///
/// A protocol for the reason every seam in this target is one: `QueryApi`, `RideApi` and `TripStateApi`
/// are Kotlin interfaces and cannot be stood in for from Swift.
protocol RideHistoryRepository: AnyObject {

    /// `GET /v1/trips/{driverId}` — every trip this driver drove, newest first (US-8.7).
    func trips(driverId: String) async throws -> [TripSummary]

    /// `GET /v1/trips/{driverId}/{tripId}` — distance, duration and *"have I rated this?"*.
    ///
    /// `distanceKm` is absent on the `aggregate_1m` geometry source and the screen prints nothing rather than
    /// a lower bound presented as a measurement — that source's own remark is explicit that the two
    /// grains differ by an order of magnitude.
    func detail(driverId: String, tripId: String) async throws -> TripDetail

    /// `GET /v1/rides/{rideId}` — who the driver is about to rate.
    ///
    /// Only a Mode C ride has one nameable passenger. `riderId` is **`nil` on a proxy booking for an
    /// unregistered rider** (P-01) — there is no `iam.users` row to hang a rating on — and
    /// `DriverRatingInput.passengerId` is required, so the sheet says so rather than sending a rating
    /// about nobody.
    func rideParties(rideId: String) async throws -> RideDetail

    /// `POST /v1/sessions/{subjectId}/driver-rating` — 1–5 stars and an optional comment (US-18.2).
    ///
    /// See this type's own note: it is the platform's **only** driver-rates-passenger route, and its
    /// path is session-scoped.
    func ratePassenger(subjectId: String, passengerId: String, stars: Int, comment: String?) async throws -> Rating
}

/// ``RideHistoryRepository`` over `:shared`'s query, ride and trip-state clients.
final class ApiRideHistoryRepository: RideHistoryRepository {

    private let query: QueryApi
    private let ride: RideApi
    private let tripState: TripStateApi

    init(query: QueryApi, ride: RideApi, tripState: TripStateApi) {
        self.query = query
        self.ride = ride
        self.tripState = tripState
    }

    /// **`Page<T>.items` arrives as `[Any]`** — the export emits the property as `NSArray<id>`, so
    /// `Page<T>` keeps its type parameter across the bridge and its rows do not. `compactMap`
    /// rather than `as!` for the reason C091 wrote down: a failed force cast raises an exception Swift
    /// cannot catch and the process terminates. Every row is a `TripSummary` by contract.
    func trips(driverId: String) async throws -> [TripSummary] {
        try await query.listTrips(userId: driverId, page: PageRequest.companion.FIRST)
            .items.compactMap { $0 as? TripSummary }
    }

    func detail(driverId: String, tripId: String) async throws -> TripDetail {
        try await query.getTrip(userId: driverId, tripId: tripId)
    }

    func rideParties(rideId: String) async throws -> RideDetail {
        try await ride.getRide(rideId: rideId)
    }

    func ratePassenger(
        subjectId: String,
        passengerId: String,
        stars: Int,
        comment: String?
    ) async throws -> Rating {
        let text = comment?.trimmingCharacters(in: .whitespacesAndNewlines)
        return try await tripState.rateSessionPassenger(
            sessionId: subjectId,
            request: DriverRatingInput(
                stars: Int32(stars),
                text: (text?.isEmpty ?? true) ? nil : text,
                passengerId: passengerId
            ),
            idempotencyKey: nil
        )
    }
}
