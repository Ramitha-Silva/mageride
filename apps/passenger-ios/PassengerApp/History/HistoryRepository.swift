import Foundation
import MageRideShared

/// Cluster 5's seam — what a passenger has already done, and the parcel that is still moving.
///
/// **Two lists come from two services and that is not duplication.** `GET /v1/rides/history` is
/// ride-svc's and carries the *ride*'s terminal state, its driver and — since AL-36/US-24.4 — the
/// post-trip Call block; `GET /v1/trips/{userId}/{tripId}` is query-svc's and is the only read that
/// carries the polyline and the filtered distance, and it spans **all three modes**, because a Mode A
/// bus journey is a trip a passenger took and is not a ride. SCR-PI-022's **Past** tab is the ride
/// history and SCR-PI-023's detail is query-svc's; collapsing them would lose one or the other.
///
/// **`ride(rideId:)` is here as well as on ``RideRepository`` and that is deliberate.** C098's door is
/// the *active* ride's — it also carries the cancel, the four fare operations and the call — and this
/// one is a history screen asking a finished ride for the one field AL-48 put on it. Both are two
/// lines over the same `RideApi.getRide`; a screen group reaching across into another cluster's
/// repository for one method is the seam this file exists to avoid. `apps/passenger-android`'s
/// `HistoryRepository` makes the same split.
///
/// A Swift protocol rather than the Kotlin clients themselves, for the rule
/// `apps/passenger-ios/CLAUDE.md` states: `RideApi`, `QueryApi` and `DispatchApi` are Kotlin
/// interfaces with `suspend` methods and Swift can stand in for none of them. **No caching and no
/// state** — the screens' state is theirs.
protocol HistoryRepository: AnyObject {

    /// `GET /v1/rides/history` — terminal rides, newest first, with the post-trip Call block.
    ///
    /// The page is unwrapped here rather than at a screen: SCR-PI-022 draws one list and there is no
    /// *"load more"* control on either wireframe, so a cursor nothing reads would be a field two
    /// models had to carry. `PageRequest.companion.FIRST` is `?limit=20`.
    func rides() async throws -> [RideHistoryRow]

    /// `GET /v1/trips/{userId}/{tripId}` — SCR-PI-023's polyline, distance and rating.
    func trip(userId: String, tripId: String) async throws -> TripDetail

    /// `GET /v1/rides/{rideId}` — the ride behind a history card or a package deep link.
    ///
    /// This is what SCR-PI-022's **Call** needs: a list row carries `mobileMasked`, which
    /// `PhoneMasked`'s own KDoc says must never be parsed back into a dialable number. The clear
    /// number is `RideDetail.counterpartyPhone` (AL-48) and only this read has it.
    func ride(rideId: String) async throws -> RideDetail

    /// The passenger's own upcoming scheduled rides — SCR-PI-022's **Scheduled** tab.
    ///
    /// **There is no such read**, and the implementation says so rather than inventing one. See
    /// ``ApiHistoryRepository/scheduled(userId:)``.
    func scheduled(userId: String) async throws -> [ScheduledRideRow]
}

/// One row of SCR-PI-022's **Scheduled** tab.
///
/// A local shape rather than `dispatch.ScheduledRide` because the passenger-facing read does not
/// exist — see ``ApiHistoryRepository/scheduled(userId:)``. `apps/passenger-android` carries the same
/// four fields for the same reason.
struct ScheduledRideRow: Identifiable, Equatable {

    let scheduledRideId: String
    var pickupLabel: String?
    var dropoffLabel: String?

    /// When the ride is booked for, in Colombo — see ``TripLabels``.
    var pickupTime: Date

    var id: String { scheduledRideId }
}

/// ``HistoryRepository`` over the generated clients.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the Objective-C export — which is why
/// `listRideHistory()` is spelled with `PageRequest.companion.FIRST` here rather than called bare.
final class ApiHistoryRepository: HistoryRepository {

    /// Named `rideApi` rather than `rides` because ``rides()`` is a method on this type: Swift would
    /// have to disambiguate a property and a function of the same base name at every use, and the
    /// resolution is not something to leave to a compiler nobody here can run.
    private let rideApi: RideApi
    private let query: QueryApi

    /// dispatch-svc, held and unused. See ``scheduled(userId:)`` — the day the passenger-facing list
    /// exists this is the client it lands on, and holding it is what makes that one line rather than
    /// a constructor change in `PassengerGraph`.
    private let dispatch: DispatchApi

    init(rides: RideApi, query: QueryApi, dispatch: DispatchApi) {
        self.rideApi = rides
        self.query = query
        self.dispatch = dispatch
    }

    /// **`Page<T>.items` arrives as `[Any]`.** The export emits the property as `NSArray<id>` —
    /// `Page<T>` keeps its type parameter across the bridge and its rows do not — so the concrete
    /// type is put back here. `compactMap` rather than `as!` for this target's own reason: a failed
    /// force cast raises an exception Swift cannot catch and the process terminates. Every row is a
    /// `RideHistoryRow` by contract, so nothing is dropped.
    func rides() async throws -> [RideHistoryRow] {
        try await rideApi.listRideHistory(page: PageRequest.companion.FIRST)
            .items.compactMap { $0 as? RideHistoryRow }
    }

    func trip(userId: String, tripId: String) async throws -> TripDetail {
        try await query.getTrip(userId: userId, tripId: tripId)
    }

    func ride(rideId: String) async throws -> RideDetail {
        try await rideApi.getRide(rideId: rideId)
    }

    /// SCR-PI-022's **Scheduled** tab, and a contract gap.
    ///
    /// `dispatch.yaml` offers `GET /v1/rides/scheduled/{driverId}` — *"what this driver has already
    /// claimed"* — and `DELETE /v1/rides/schedule/{scheduledRideId}` to withdraw one. **There is no
    /// read that answers "what has this passenger scheduled".** A passenger can create a scheduled
    /// ride (C097's SCR-PI-013) and cancel one by id, and cannot list their own.
    ///
    /// Answering empty rather than inventing a call: the tab renders its empty state, which is
    /// honest, and the moment the route exists this becomes one line. C081 recorded the same gap from
    /// the Android side and this is the same answer, so neither app is the one that guessed.
    func scheduled(userId: String) async throws -> [ScheduledRideRow] {
        []
    }
}

extension RideHistoryRow {

    /// Whether this row's driver can be reached (US-24.4, AL-48).
    ///
    /// **`false` for a ride cancelled before assignment**, which is this component's fence — *"real
    /// driver number on completed trips; withheld if cancelled pre-assignment"*. There was never a
    /// driver: the row carries no `driver` block, and the state says which kind of cancel it was.
    var hasReachableDriver: Bool {
        driver != nil
            && state != RideState.cancelledbyriderbeforeaccept
            && state != RideState.expirednodriver
    }

    /// Whether this row belongs on the **Packages** tab.
    ///
    /// **`RideHistoryRow` carries no `kind`**, so the only signal is the terminal state a parcel
    /// reaches: `CashOnDeliveryCollected` is P-08's and nothing but a package gets there. A passenger
    /// ride and a package paid by any other rail are indistinguishable in this row — C081 recorded it
    /// and the C099 handoff restates it, because adding `kind` to the row is a `ride.yaml` change
    /// rather than an app change.
    var isPackage: Bool { state == RideState.cashondeliverycollected }
}
