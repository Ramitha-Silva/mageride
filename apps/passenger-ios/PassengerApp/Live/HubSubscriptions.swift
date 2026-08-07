import Foundation
import MageRideShared

/// Everything this client is subscribed to on `/hubs/live`, and every rule for changing it.
///
/// **Three memberships, three lifetimes.** The geocells follow the passenger and are held still for
/// thirty seconds after a boundary crossing (ADD §7.4 step 6); a ride group lives as long as the
/// ride; a location-request group lives for P-02's three hundred seconds. Keeping all three here —
/// rather than beside the socket — is what makes the reconnect plan a *read* of this object instead
/// of state scattered across the connection loop.
///
/// **Nothing here subscribes to a vehicle.** `vehicle:{vehicleId}` groups exist, and
/// `signalr-hub.md` §2.1 says they are *"joined by the server, never asked for"*: a Mode B vehicle
/// appears because fanout-svc checked the `share:{userId}` entitlement at join (D-23), not because a
/// client asked. There is no `SubscribeVehicle` method, and this type is where inventing one would
/// have to happen.
///
/// **An `actor`, which is what the Android twin's `Mutex` is for.** `GeoCellSubscription`'s own KDoc
/// says to drive it from one coroutine, and a position fix, a ride and a reconnect all arrive from
/// different ones. It also holds a non-`Sendable` Kotlin object, which is the shape C093 recorded
/// for `DriverDatabase` and the first thing `SWIFT_STRICT_CONCURRENCY` will surface when it is
/// raised.
actor HubSubscriptions {

    private let grid: H3Grid
    private let geocells: GeoCellSubscription
    private let contract = IosLiveHub()
    private let now: () -> Timestamp
    private let send: (String, HubArgument) async -> Void

    /// Insertion-ordered, like the Android `LinkedHashSet`s: the recovery plan replays them in the
    /// order they were asked for, which is the order a passenger opened them in.
    private var rides: [String] = []
    private var locationRequests: [String] = []

    /// The cells currently joined. **Nineteen** once a position has been fed (R-06).
    private(set) var cells: Set<H3Cell> = []

    /// - Parameters:
    ///   - grid: The platform H3 engine. Cell ids must be bit-identical to the ones
    ///     `position-processor-svc` computes, or the client joins groups nothing publishes to — a
    ///     failure that looks exactly like an empty map with no error anywhere.
    ///   - now: Wall clock, injected: the hysteresis is a comparison against it, and a test that
    ///     could only wait for real time would have to sleep for thirty seconds to assert the rule.
    ///   - send: The hub invocation. Wrapped by the caller so a send on a dead socket is a no-op —
    ///     group membership is re-established wholesale by ``restore()`` on the next connect.
    init(
        grid: H3Grid,
        now: @escaping () -> Timestamp = { IosInstantKt.nowTimestamp() },
        send: @escaping (String, HubArgument) async -> Void
    ) {
        self.grid = grid
        // R-06's view and ADD §7.4 step 6's window, built on the Kotlin side because the hysteresis
        // is a `Duration` — see `IosGeoCellsKt`.
        self.geocells = IosGeoCellsKt.passengerCellSubscription(grid: grid)
        self.now = now
        self.send = send
    }

    /// Feeds a position fix — the R-06 subscription's only input.
    ///
    /// The first fix joins nineteen cells. Every later one is compared against the cell already
    /// being served: staying inside it changes nothing, and a crossing is applied at most once per
    /// `GeoCells.BOUNDARY_HYSTERESIS` (30 s) so a passenger standing on a cell edge does not thrash
    /// group membership — every join and leave is a `RemoveFromGroupAsync` on the backplane. All
    /// three rules are `GeoCellSubscription`'s; this only sends what it decides.
    ///
    /// - Returns: The new cell set when membership changed, `nil` when it did not.
    @discardableResult
    func onPosition(_ point: GeoPoint) async -> Set<H3Cell>? {
        await apply(geocells.onPosition(point: point, now: now()))
    }

    /// Re-evaluates a held boundary crossing without a new fix.
    ///
    /// A passenger who crosses a cell edge and then **stops moving** still has to end up subscribed
    /// to the right nineteen cells, and a stationary handset is exactly when Core Location stops
    /// producing fixes. The map's own tick calls this.
    @discardableResult
    func refresh() async -> Set<H3Cell>? {
        await apply(geocells.refresh(now: now()))
    }

    /// `SubscribeRide` — the caller's own ride (US-6A.12). Rejoined by ``restore()`` on every
    /// reconnect.
    func watchRide(_ rideId: String) async {
        guard !rides.contains(rideId) else { return }
        rides.append(rideId)
        await send(contract.methodSubscribeRide, .text(rideId))
    }

    /// Stops rejoining [rideId]. The hub has no unsubscribe for a ride group; this is the client
    /// half, and it is what stops a finished ride being re-joined for the life of the process.
    func stopWatchingRide(_ rideId: String) {
        rides.removeAll { $0 == rideId }
    }

    /// `SubscribeLocRequest` — the booker awaiting a rider's confirmation (P-13).
    func watchLocationRequest(_ requestId: String) async {
        guard !locationRequests.contains(requestId) else { return }
        locationRequests.append(requestId)
        await send(contract.methodSubscribeLocRequest, .text(requestId))
    }

    /// Stops rejoining [requestId]. Called on resolve, or on the 300 s expiry (P-02).
    func stopWatchingLocationRequest(_ requestId: String) {
        locationRequests.removeAll { $0 == requestId }
    }

    /// D6' §5.4 / `signalr-hub.md` §1.1 — rejoins every group after a reconnect.
    ///
    /// `GeoCellSubscription.onReconnected()` deliberately ignores the hysteresis window: after a
    /// drop the server holds no membership at all, and rate-limiting recovery would leave the map
    /// blank for up to thirty seconds.
    ///
    /// **The order is the contract, not a preference.** `LiveHubRecovery.plan` puts the joins before
    /// the snapshot because a client that snapshots *first* loses every frame published between the
    /// two calls — exactly the ones that moved while it was away. The plan is followed verbatim;
    /// nothing here decides an order of its own.
    ///
    /// - Returns: `true` when the caller should now resync from `GET /v1/nearby`.
    func restore() async -> Bool {
        let restored = geocells.onReconnected()
        cells = restored.cells

        let plan = LiveHubRecovery.shared.plan(
            scope: LiveMapScopePassengerView(cells: restored.cells),
            activeRides: Set(rides),
            pendingLocationRequests: Set(locationRequests)
        )

        var snapshotDue = false
        for step in plan {
            switch step {
            case let join as RecoveryStepJoinGeocells:
                await send(contract.methodJoinGeocells, .texts(IosGeoCellsKt.cellTokens(cells: join.cells)))
            case let ride as RecoveryStepSubscribeRide:
                await send(contract.methodSubscribeRide, .text(ride.rideId))
            case let request as RecoveryStepSubscribeLocationRequest:
                await send(contract.methodSubscribeLocRequest, .text(request.requestId))
            case is RecoveryStepResyncNearbySnapshot:
                snapshotDue = true
            default:
                // A step this build does not know is a contract addition, and skipping it is the
                // honest outcome — the alternative is a `fatalError` on a deploy.
                continue
            }
        }
        return snapshotDue
    }

    /// Forgets everything. The passenger signed out, or the app is going.
    func reset() {
        geocells.reset()
        rides.removeAll()
        locationRequests.removeAll()
        cells = []
    }

    private func apply(_ update: CellSubscriptionUpdate) async -> Set<H3Cell>? {
        cells = update.cells
        guard update.changed else { return nil }

        // Leave first, then join. The two sets are disjoint, so the order is not a correctness
        // matter — but leaving first is what keeps the server's per-connection group count at
        // nineteen rather than briefly at twenty-five while a crossing is applied.
        if !update.leave.isEmpty {
            await send(contract.methodLeaveGeocells, .texts(IosGeoCellsKt.cellTokens(cells: update.leave)))
        }
        if !update.join.isEmpty {
            await send(contract.methodJoinGeocells, .texts(IosGeoCellsKt.cellTokens(cells: update.join)))
        }
        return update.cells
    }
}
