import Combine
import Foundation
import MageRideShared

/// Where the live plane is. Feeds SCR-PI-032's banner alongside ``ConnectivityMonitor``.
enum LiveStatus {
    /// No socket, and none being opened — nobody has called ``PassengerLiveMap/connect()`` yet.
    case disconnected

    /// Dialling, or waiting out a backoff before dialling again.
    case connecting

    /// Up, groups joined, snapshot back-filled.
    case connected
}

/// The passenger's whole real-time plane: one `/hubs/live` connection for the process.
///
/// Three concerns, three objects, and this is the one that owns the socket:
///
/// - ``HubSubscriptions`` — which groups the client is in: R-06's nineteen cells, the 30 s boundary
///   hysteresis, and D6' §5.4's recovery plan.
/// - ``LiveHubInbox`` — what the server said, decoded into the map's own state, whether it arrived
///   as a socket frame or as a `GET /v1/nearby` snapshot.
/// - this class — dialling, staying dialled, and the facade a screen sees.
///
/// **Reconnection is ours.** R-09's curve is a platform rule rather than a client setting — see
/// ``SignalRLiveHubTransport`` — so ``connect()`` runs a supervision loop over `:shared`'s
/// `ReconnectBackoff` through `IosReconnectBackoff`: jittered exponential, 1–60 s, ±25 %, the same
/// curve the driver app's MQTT client uses, because a regional outage ends for both planes at the
/// same instant. The first retry therefore lands inside 1.25 s, which is what makes the recovery fit
/// in the five seconds SCR-PI-032 promises.
///
/// `@MainActor` and an `ObservableObject`, because everything it publishes is read by a view. The
/// work behind those properties is on two actors; this class is the hop.
@MainActor
final class PassengerLiveMap: ObservableObject {

    /// What is on the map. Replaced wholesale per batch — see ``LiveVehicleStore``.
    @Published private(set) var vehicles: [VehicleFrame] = []

    /// The cells currently joined. **Nineteen** once a position has been fed (R-06).
    @Published private(set) var cells: Set<H3Cell> = []

    /// Where the socket is.
    @Published private(set) var status: LiveStatus = .disconnected

    /// The four directed payloads. A `PassthroughSubject` rather than a `@Published` because these
    /// are *events* — a screen that subscribes late must not be handed the last ride transition as
    /// though it had just happened.
    let events = PassthroughSubject<LiveEvent, Never>()

    private let transport: LiveHubTransport
    private let contract = IosLiveHub()
    private let newBackoff: () -> IosReconnectBackoff

    private var subscriptions: HubSubscriptions!
    private var inbox: LiveHubInbox!

    /// Signalled by the transport's `onClosed`; awaited by the supervision loop.
    private let closed = CloseSignal()

    private var supervisor: Task<Void, Never>?
    private var lastPoint: GeoPoint?

    /// - Parameters:
    ///   - transport: The socket. Faked in tests; see ``LiveHubTransport``.
    ///   - snapshots: query-svc's `GET /v1/nearby`, for the snapshot half of the recovery.
    ///   - grid: The platform H3 engine. Cell ids must be bit-identical to the ones
    ///     `position-processor-svc` computes, or the client joins groups nothing publishes to — a
    ///     failure that looks exactly like an empty map with no error anywhere.
    ///   - now: Wall clock, injected — the boundary hysteresis is a comparison against it.
    ///   - newBackoff: Overridable so a test can assert the curve rather than sleep through it.
    init(
        transport: LiveHubTransport,
        snapshots: NearbySnapshots,
        grid: H3Grid,
        now: @escaping () -> Timestamp = { IosInstantKt.nowTimestamp() },
        newBackoff: @escaping () -> IosReconnectBackoff = { IosReconnectBackoff() }
    ) {
        self.transport = transport
        self.newBackoff = newBackoff

        self.subscriptions = HubSubscriptions(grid: grid, now: now) { [transport] method, argument in
            // Dropped rather than thrown when the socket is down: every send this app makes is a
            // group membership, and group membership is re-established wholesale by `restore()` on
            // the next connect. Throwing here would make each caller handle a case that already has
            // one answer.
            await transport.send(method, argument)
        }

        self.inbox = LiveHubInbox(
            snapshots: snapshots,
            grid: grid,
            onVehicles: { [weak self] frames in
                Task { @MainActor in self?.vehicles = frames }
            },
            onEvent: { [weak self] event in
                Task { @MainActor in self?.events.send(event) }
            }
        )
    }

    /// Opens the connection and keeps it open until ``disconnect()``.
    ///
    /// Idempotent: a second call while a supervisor is already running does nothing, which is what
    /// lets the shell call it from `.task` without tracking whether it already has.
    func connect() {
        guard supervisor == nil else { return }
        supervisor = Task { [weak self] in await self?.supervise() }
    }

    /// Closes the connection and forgets the map. The passenger signed out, or the app is going.
    func disconnect() async {
        supervisor?.cancel()
        supervisor = nil
        await transport.close()
        lastPoint = nil
        await subscriptions.reset()
        await inbox.clear()
        cells = []
        status = .disconnected
    }

    /// Feeds a position fix — the R-06 subscription's only input. See
    /// ``HubSubscriptions/onPosition(_:)``.
    func onPosition(_ point: GeoPoint) {
        lastPoint = point
        Task { [subscriptions, inbox] in
            guard let changed = await subscriptions?.onPosition(point) else { return }
            await inbox?.retain(cells: changed)
            await MainActor.run { self.cells = changed }
        }
    }

    /// Re-evaluates a held boundary crossing without a new fix. See ``HubSubscriptions/refresh()``.
    ///
    /// A passenger who crosses a cell edge and then **stops moving** is exactly when Core Location
    /// stops producing fixes, and the held crossing would never land. SCR-PI-010's own tick calls
    /// this.
    func refreshCells() {
        Task { [subscriptions, inbox] in
            guard let changed = await subscriptions?.refresh() else { return }
            await inbox?.retain(cells: changed)
            await MainActor.run { self.cells = changed }
        }
    }

    /// Drops a vehicle the passenger has just unsubscribed from (AL-25, US-4.11).
    ///
    /// **Not a hub call.** `signalr-hub.md` §2 has four client → server methods and none of them
    /// leaves a `vehicle:{vehicleId}` group — membership is the server's, granted at join from the
    /// `share:{userId}` entitlement (D-23). What this does is erase the marker already drawn, so
    /// *"unsubscribing removes the vehicle from the live map within seconds"* is true on the tap
    /// rather than on the round trip. fanout-svc's `share.revoked` arrives behind it and finds
    /// nothing left to remove, which is the correct no-op. See ``LiveVehicleStore/drop(_:)``.
    func dropVehicle(_ vehicleId: String) {
        Task { [inbox] in await inbox?.drop(vehicleId: vehicleId) }
    }

    /// `SubscribeRide` — the caller's own ride (US-6A.12). Rejoined on every reconnect.
    func watchRide(_ rideId: String) {
        Task { [subscriptions] in await subscriptions?.watchRide(rideId) }
    }

    /// Stops rejoining [rideId] on reconnect.
    func stopWatchingRide(_ rideId: String) {
        Task { [subscriptions] in await subscriptions?.stopWatchingRide(rideId) }
    }

    /// `SubscribeLocRequest` — the booker awaiting a rider's confirmation (P-13).
    func watchLocationRequest(_ requestId: String) {
        Task { [subscriptions] in await subscriptions?.watchLocationRequest(requestId) }
    }

    /// Stops rejoining [requestId]. Called on resolve, or on the 300 s expiry (P-02).
    func stopWatchingLocationRequest(_ requestId: String) {
        Task { [subscriptions] in await subscriptions?.stopWatchingLocationRequest(requestId) }
    }

    // MARK: - The supervision loop

    private func supervise() async {
        let backoff = newBackoff()

        while !Task.isCancelled {
            status = .connecting

            let opened: Bool
            do {
                try await transport.connect(
                    events: contract.eventNames,
                    onEvent: { [weak self] name, payload in
                        guard let self else { return }
                        Task { await self.inbox?.receive(event: name, payload: payload) }
                    },
                    onClosed: { [closed] _ in
                        Task { await closed.signal() }
                    }
                )
                opened = true
            } catch {
                opened = false
            }

            if !opened {
                await sleep(seconds: backoff.nextSeconds())
                continue
            }

            // Opening a connection closes the previous one, and *that* fires its own `onClosed`.
            // Without this drain the signal from the socket just replaced would be waiting and the
            // wait below would return immediately — a healthy connection torn down one frame after
            // it came up, for ever. C076 found this on the Android side; the transport also drops
            // the replaced connection's delegate, which is the belt to this braces.
            await closed.drain()

            backoff.reset()
            status = .connected
            await recover()

            // Suspends until the transport reports a drop. Nothing polls: SignalR's own keep-alive
            // (15 s) and server timeout (30 s) are what detect a dead socket, and both are set on
            // the connection from `IosLiveHub`'s values.
            await closed.wait()
            if Task.isCancelled { break }

            status = .disconnected
            await sleep(seconds: backoff.nextSeconds())
        }
    }

    /// D6' §5.4 — rejoin the groups, **then** snapshot. The order is the contract; see
    /// ``HubSubscriptions/restore()``.
    private func recover() async {
        let snapshotDue = await subscriptions.restore()
        cells = await subscriptions.cells
        guard snapshotDue, let point = lastPoint else { return }
        await inbox.resync(around: point)
    }

    private func sleep(seconds: Double) async {
        try? await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
    }
}

/// A one-slot "the socket dropped" signal.
///
/// This is `Channel(CONFLATED)` on the Android side, and the three operations are the ones that loop
/// needs: signal from a callback that may fire with nobody waiting, wait for the next one, and
/// **drain** whatever is pending. An `AsyncStream` cannot express the drain, and a bare
/// `CheckedContinuation` deadlocks whenever the drop arrives before the loop gets round to awaiting
/// it — which is exactly what a handshake that fails immediately does.
private actor CloseSignal {

    private var pending = false
    private var waiter: CheckedContinuation<Void, Never>?

    func signal() {
        if let waiter {
            self.waiter = nil
            waiter.resume()
        } else {
            pending = true
        }
    }

    func wait() async {
        if pending {
            pending = false
            return
        }
        await withCheckedContinuation { continuation in
            waiter = continuation
        }
    }

    /// Throws away a signal nobody is waiting for. Called after a successful connect.
    func drain() {
        pending = false
    }
}
