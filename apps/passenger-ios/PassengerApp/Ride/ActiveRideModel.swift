import Combine
import Foundation
import MageRideShared

/// Where a ride sends the passenger when it stops being a ride.
///
/// **This exists because nothing carried them there.** Both wireframes say the ride hands over —
/// SCR-PI-016's own cell is drawn as the screen after a completed trip — and `PassengerRoute`'s
/// comment on ``PassengerRoute/paymentMethod(rideId:)`` says the same; but on the Android side no
/// screen, no `NavHost` arm and no push builds that destination, so `Completed` left the passenger
/// sitting on a finished ride and the whole of D-10 was reachable only from the receipt it comes
/// *after*. Recorded in the C098 handoff as a defect found in C080.
enum RideHandOff: Equatable {

    /// `Completed` / `PaymentPending` — D-10 starts. SCR-PI-016.
    case payment

    /// Already settled: cash taken in the vehicle, or a wallet fare that landed while the app was
    /// away. There is nothing to pay, so the passenger gets the receipt. SCR-PI-018.
    case receipt

    /// Cancelled, or a no-show. Nothing left to watch and nothing to pay — back to the map.
    case finished
}

/// SCR-PI-014 and SCR-PI-015's shared state.
struct ActiveRideState {

    /// The aggregate. `nil` until the first read lands.
    var ride: RideDetail?

    /// The assigned driver's live marker (US-6A.12), from the SignalR plane.
    var driverPosition: GeoPoint?

    /// SCR-PI-014's `1:34 left`. Zero once the two minutes are up.
    var secondsUntilTimeout = 0

    /// The two-minute timeout fired (US-6A.11) — *"No drivers available"* plus a retry.
    var noDriver = false

    var isCancelling = false

    /// The confirm dialog is open. What cancelling **costs** is ``cancelIsFree``, and it is stated
    /// there before the tap.
    var isPendingCancel = false

    /// D-05's Rs 50, once a cancel has actually incurred one. The **server's** figure.
    var penalty: CancellationPenalty?

    /// Where the ride is sending the passenger next, once it is going anywhere.
    var handOff: RideHandOff?

    var errorKey: String?

    /// SCR-PI-014's `1:34`.
    var countdown: String {
        String(format: "%d:%02d", secondsUntilTimeout / 60, secondsUntilTimeout % 60)
    }

    /// Whether a driver has been assigned — the line between SCR-PI-014 and SCR-PI-015.
    var isAssigned: Bool { ride?.driver != nil }

    /// Whether cancelling now costs the passenger anything (US-6A.9 versus US-6A.10).
    ///
    /// Free before acceptance, Rs 50 after — and the dialog says which **before** the tap, because a
    /// fee a passenger discovers afterwards is a fee they will contest.
    var cancelIsFree: Bool {
        guard let state = ride?.state else { return true }
        return state == RideState.requested || state == RideState.matching || state == RideState.offered
    }

    /// The driver's real number, from acceptance onward (AL-48). `nil` before that, which greys the
    /// direct-dial row rather than hiding it.
    var driverPhone: String? { ride?.counterpartyPhone }

    /// The wireframe's `Driver arriving · 3 min` pill. `nil` before there is an ETA to state.
    var etaMinutes: Int32? {
        guard let seconds = ride?.driver?.etaSeconds?.int32Value, seconds > 0 else { return nil }
        return (seconds + 59) / 60
    }
}

/// SCR-PI-014 and SCR-PI-015 — one ride, two faces.
///
/// They are one model because they are one aggregate: *"Finding a driver…"* and *"Driver arriving ·
/// 3 min"* are `Matching` and `Accepted` on the same row, and splitting them would mean two
/// subscriptions to the same SignalR group and two polls of the same endpoint. The **screens** are
/// separate because the wireframe draws them separately and the back stack treats them differently —
/// SCR-PI-014 is *replaced* by SCR-PI-015, never stacked under it.
///
/// **The socket is the channel and the poll is the floor.** `RideStateChanged` and `DriverPosition`
/// arrive over the plane C094 owns; the poll exists because a ride is the one thing in this app a
/// passenger cannot afford to be wrong about, and because the two-minute timeout has to fire even on
/// a handset whose socket died in a tunnel.
///
/// **Cancelling after acceptance costs Rs 50 and the passenger is told so first.** D-05 carries the
/// debt to the *next* trip rather than charging now, which is exactly why it has to be said out loud:
/// nothing appears on a statement today, and an unwarned passenger meets it three weeks later as a
/// surprise.
@MainActor
final class ActiveRideModel: ObservableObject {

    @Published private(set) var state = ActiveRideState()

    private let rideId: String
    private let rides: RideRepository
    private let live: PassengerLiveMap
    private let now: () -> Date

    /// How often the poll re-reads. Injected so a test can assert the floor without sleeping through
    /// it — the same reason `PassengerLiveMap` takes its backoff.
    private let pollInterval: TimeInterval

    /// The countdown's tick. Injected for the same reason; production is one second.
    private let tickInterval: TimeInterval

    private var subscriptions: Set<AnyCancellable> = []
    private var work: [Task<Void, Never>] = []
    private var startedAt: Date?

    init(
        rideId: String,
        rides: RideRepository,
        live: PassengerLiveMap,
        now: @escaping () -> Date = Date.init,
        pollInterval: TimeInterval = 10,
        tickInterval: TimeInterval = 1
    ) {
        self.rideId = rideId
        self.rides = rides
        self.live = live
        self.now = now
        self.pollInterval = pollInterval
        self.tickInterval = tickInterval
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Joins the ride's group, reads it, and starts the socket, the poll and the countdown.
    ///
    /// Idempotent, so both screens can call it from `.task` and a re-entry after a scene change costs
    /// nothing. `SubscribeRide` is the **caller's own** ride — `signalr-hub.md` §2.1 — and is
    /// rejoined by ``PassengerLiveMap`` on every reconnect.
    func start() {
        guard startedAt == nil else { return }
        startedAt = now()

        live.watchRide(rideId)
        watchEvents()

        work.append(Task { await self.refresh() })
        work.append(Task { await self.poll() })
        work.append(Task { await self.countDown() })
    }

    /// Leaves the group and stops every loop. The screen went away.
    func stop() {
        live.stopWatchingRide(rideId)
        work.forEach { $0.cancel() }
        work = []
        subscriptions = []
        startedAt = nil
    }

    /// Re-reads the aggregate.
    func refresh() async {
        do {
            let detail = try await rides.ride(rideId: rideId)
            guard !Task.isCancelled else { return }
            state.ride = detail
            state.errorKey = nil
            state.noDriver = state.noDriver || detail.state == RideState.expirednodriver
            state.handOff = ActiveRideModel.handOff(for: detail.state)
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = RideErrors.messageKey(for: error)
        }
    }

    /// The `✕` was tapped. Opens the confirm rather than cancelling — see the type's note.
    func askToCancel() {
        state.isPendingCancel = true
    }

    func dismissCancel() {
        state.isPendingCancel = false
    }

    /// Confirmed — `POST /v1/rides/{rideId}/cancel`.
    ///
    /// The response's `penalty` is kept in state so the screen can say what was incurred: D-05
    /// settles on the next trip, so this is the only moment the passenger is told a number, and the
    /// number they are told afterwards is the **server's** rather than the dialog's constant.
    func confirmCancel() {
        guard let ride = state.ride, !state.isCancelling else { return }

        state.isCancelling = true
        state.isPendingCancel = false
        state.errorKey = nil

        work.append(Task {
            do {
                let answer = try await rides.cancel(
                    rideId: rideId,
                    version: ride.version,
                    reason: RideCancelReason.riderChangedMind
                )
                guard !Task.isCancelled else { return }
                state.isCancelling = false
                state.penalty = answer.penalty
                state.handOff = .finished
            } catch is CancellationError {
                return
            } catch {
                state.isCancelling = false
                state.errorKey = RideErrors.messageKey(for: error)
            }
        })
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// The socket. `RideStateChanged` re-reads; `DriverPosition` just moves the marker.
    ///
    /// A re-read rather than folding the event's own `state` in, because the event carries five
    /// fields and the screen draws twenty — and one read is cheaper than reconciling a partial
    /// update with a `RideDetail` whose `doCopy` reaches Swift as twenty-two arguments.
    private func watchEvents() {
        live.events
            .sink { [weak self] event in
                guard let self else { return }
                switch event {
                case .rideState(let payload) where payload.rideId == rideId:
                    work.append(Task { await self.refresh() })

                case .driverMoved(let payload) where payload.rideId == rideId:
                    state.driverPosition = GeoPoint(lat: payload.lat, lng: payload.lng)

                default:
                    break
                }
            }
            .store(in: &subscriptions)
    }

    /// The floor under the socket.
    ///
    /// Stops once the ride is terminal — a completed ride does not change again, and a screen that
    /// kept polling one would be a background request every ten seconds for as long as the app lived.
    private func poll() async {
        while !Task.isCancelled, state.ride?.state.isTerminalForPassenger != true {
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            guard !Task.isCancelled else { return }
            await refresh()
        }
    }

    /// US-6A.11's two minutes.
    ///
    /// Client-side, and deliberately: the timeout is a **UI promise** — *"usually under 2 min"* — and
    /// dispatch's own expiry is its business. What this drives is when the screen stops saying
    /// *finding* and starts offering a retry, which has to happen even if nothing arrives at all.
    private func countDown() async {
        let openedAt = startedAt ?? now()
        while !Task.isCancelled, !state.isAssigned, state.handOff == nil {
            let elapsed = now().timeIntervalSince(openedAt)
            let left = max(0, Int(ActiveRideModel.searchWindowSeconds - elapsed))
            state.secondsUntilTimeout = left
            if left == 0 {
                state.noDriver = true
                return
            }
            try? await Task.sleep(nanoseconds: UInt64(tickInterval * 1_000_000_000))
        }
    }

    /// Where a state sends the passenger, or `nil` while the ride is still a ride.
    ///
    /// `ExpiredNoDriver` is deliberately **not** a hand-off: SCR-PI-014 draws its own *"No drivers
    /// available"* plus a retry over the same screen, which is US-6A.11's own wording.
    private static func handOff(for rideState: RideState) -> RideHandOff? {
        switch rideState {
        case RideState.completed, RideState.paymentpending:
            return .payment

        case RideState.paid, RideState.cashsettled, RideState.cashondeliverycollected:
            return .receipt

        case RideState.cancelledbyriderbeforeaccept,
             RideState.cancelledbyriderafteraccept,
             RideState.cancelledbydriver,
             RideState.noshowrider,
             RideState.noshowdriver:
            return .finished

        default:
            return nil
        }
    }

    /// US-6A.11 — *"usually under 2 min"*, and the point at which the screen offers a retry.
    static let searchWindowSeconds: TimeInterval = 120

    /// D-05's Rs 50, in minor units.
    ///
    /// **A local constant, and it has to be.** The dialog names it *before* the call that would
    /// return it — `CancelRideResponse.penalty` arrives after the cancel has happened, which is too
    /// late to be a warning. The server's figure is what is actually owed and is what the screen
    /// reports afterwards; this one exists only so the warning can exist at all.
    static let cancellationPenaltyMinor: Int64 = 5_000
}
