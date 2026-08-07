import Foundation
import MageRideShared

/// Which of SCR-DI-015's three sheets is up. At most one, which is why this is an enum.
enum RideSheet: String, Identifiable {

    /// The rider's four-digit start OTP; a package's sender OTP on the same entry (P-07).
    case startOtp

    /// AL-48's chooser — **Free call** (in-app VoIP) or **Normal call** (a direct cellular dial).
    case callType

    /// AL-47's *"QR payment received?"* attestation (US-26.1).
    case qrConfirm

    var id: String { rawValue }
}

/// SCR-DI-015's state.
///
/// - Parameters:
///   - ride: The aggregate, as ride-svc last answered a **full** read of it.
///   - moved: The latest state and version, whether from the full read, a command's response or a
///     poll. Held **beside** ``ride`` rather than folded into it, for two reasons. `RideDetail` is a
///     Kotlin data class and its `copy` reaches Swift as a twenty-two-argument `doCopy`, so folding
///     would mean re-listing every field of the aggregate at each transition and getting one of them
///     wrong eventually. And it is the truthful shape: the state and the version are the only two
///     things that move between full reads — everything else on a ride is fixed once it is accepted.
///   - position: The driver's own last fix — the map marker and the SOS coordinate.
///   - sheet: Which sheet is up, if any.
///   - isVoipRequested: The driver chose **Free call**; the screen opens SCR-DI-031 (C093).
///   - isSosRequested: The driver tapped **SOS**; the screen opens SCR-DI-032 (C093).
///   - isQrAttested: Whether the driver has already answered AL-47's *"QR payment received?"*.
///   - isFinished: The ride has reached a state that takes the driver back to standby.
///   - isBusy: A command is in flight.
///   - errorKey: Resolved copy for the last failure.
struct ActiveRideState {

    var ride: RideDetail?
    var moved: RideStateSnapshot?
    var position: Fix?
    var sheet: RideSheet?
    var isVoipRequested = false
    var isSosRequested = false
    var isQrAttested = false
    var isFinished = false
    var isBusy = false
    var errorKey: String?

    /// Where the ride is, or `nil` before the first read.
    var rideState: RideState? { moved?.state ?? ride?.state }

    /// The version the next command must echo (R-14) — the newest one seen, from whichever read.
    var version: Int32? { moved?.version ?? ride?.version }

    /// Whether this job is a parcel rather than a passenger.
    ///
    /// A delivery is the same aggregate on the same route (R-01, and ``PushRouter`` lands both deep
    /// links here), but it is a different set of sheets: SCR-DI-016a/b/c and its two OTP handoffs,
    /// owned by C089. This screen hands over as soon as the first read says so.
    var isPackage: Bool { ride?.kind == RideKind.package }

    /// Whether the state poll should run.
    ///
    /// A ride that is over has nothing left to learn, one mid-command is about to be told by the
    /// response, one not yet read has no version to compare, and a **package** belongs to SCR-DI-016,
    /// which polls it itself.
    var isPollable: Bool { !isFinished && !isBusy && ride != nil && !isPackage }

    /// Whether this ride settles by **AL-47 attestation** rather than by a gateway callback.
    ///
    /// The passenger scanned the driver's own bank LankaQR, so the money moved bank-to-bank and
    /// nothing outside the driver's banking app ever saw it. That is the whole reason the confirm
    /// sheet exists: there is no webhook that could close this payment.
    var settlesByDriverQr: Bool { ride?.paymentMethod == RidePaymentMethod.lankaqr }

    /// Whether the driver-QR confirm is the action the ride is now waiting on (AL-47, US-26.1).
    ///
    /// **``isQrAttested`` is what closes it, not the ride's state.** Confirming settles the *payment* —
    /// `DriverConfirmedQR` is terminal on the payment machine — but the ride sits at `PaymentPending`
    /// until fare-svc's `payment-settled` reaches ride-svc through the outbox and moves it to
    /// `CashSettled`. Asking again in that window would put the sheet back up on a driver who has
    /// already answered, and a second attestation is not a thing they can give.
    var awaitingQrConfirm: Bool {
        guard settlesByDriverQr, !isQrAttested, let rideState else { return false }
        return rideState == RideState.completed || rideState == RideState.paymentPending
    }

    /// Where MAP-10's 100 m circle goes — the point the driver is being sent to *now*.
    ///
    /// The pickup until the rider is on board and the drop after, which is the same circle D5' §7
    /// evaluates `DriverArrived` against.
    /// Unwrapped before the switch rather than matched as an optional: a Kotlin enum reaches Swift as a
    /// **class**, so a `case` over one is an `Equatable` comparison rather than a case pattern. Every
    /// switch over `RideState` in this file has the same shape for the same reason.
    var geofence: GeoPoint? {
        guard let rideState else { return nil }
        switch rideState {
        case RideState.accepted, RideState.driverArrived: return ride?.pickup.point
        case RideState.inProgress: return ride?.dropoff.point
        default: return nil
        }
    }

    /// The sheet's route line — *"Galle Face → Nugegoda · Rs 480"*.
    var routeLine: String {
        guard let ride else { return "" }
        let places = [ride.pickup.address, ride.dropoff.address]
            .compactMap { $0 }
            .joined(separator: MageRideSymbols.routeArrow)
        guard let fare = ride.fare else { return places }
        return places + MageRideSymbols.separator + MoneyFormat.rupees(fare.amountMinor)
    }

    /// The banner above the map — where the driver is being sent next.
    var navigationHintKey: String {
        guard let rideState else { return "ride_finished" }
        switch rideState {
        case RideState.accepted, RideState.driverArrived: return "ride_navigate_pickup"
        case RideState.inProgress: return "ride_navigate_drop"
        default: return "ride_finished"
        }
    }

    /// The forward CTA's label, from the ride's own state.
    ///
    /// `nil` means there is no forward move from here — a terminal ride, or a driver-QR ride waiting
    /// on AL-47's confirm, which is a different button.
    var forwardActionKey: String? {
        guard let rideState else { return nil }
        switch rideState {
        case RideState.accepted: return "ride_arrived"
        case RideState.driverArrived: return "ride_start"
        case RideState.inProgress: return "ride_complete"
        default: return nil
        }
    }

    /// Folds a server-confirmed state onto the screen.
    ///
    /// The two rules a plain assignment would get wrong: a **driver-QR ride is not over at
    /// `PaymentPending`** — AL-47's confirm is what settles it, so the sheet comes up instead of the
    /// screen closing — and ``isFinished`` is the ride's own `isTerminal`, never a guess from which
    /// command was just sent.
    mutating func advance(to snapshot: RideStateSnapshot) {
        isBusy = false
        moved = snapshot

        if awaitingQrConfirm { sheet = .qrConfirm }
        // Sticky, because terminal is terminal: a ride that has finished never un-finishes, and a
        // late poll landing after the driver has been sent back to standby must not undo that.
        isFinished = isFinished || (snapshot.state.isTerminal && !awaitingQrConfirm)
    }
}

/// **SCR-DI-015 · the active ride** — accepted through to payment.
///
/// **The client never advances a ride.** `RideProjection` moves only on a server-confirmed state, and
/// `canSend(command)` is the other direction: a *local* guess about whether a command is legal from
/// here, used to decide which CTA the sheet draws. It is never a claim about what ride-svc would allow
/// — ride-svc is the sole writer (R-01) and its answer is applied whatever the table says.
///
/// **Every mutation echoes the version the screen was showing** (R-14). A `409 version-conflict` means
/// somebody else moved the ride, so the answer is to re-read and let the driver decide again — never
/// to bump the number and retry.
///
/// **The state poll is the documented fallback, and here it is the only path.** D3' §3.1 puts ride
/// transitions on the SignalR hub with `GET /v1/rides/{rideId}/state` as the backplane-down fallback
/// (D6' §8.3). `:shared` carries the hub's contract (`LiveHub`) and **no client for it**, so this
/// screen polls. See the C070 handoff.
@MainActor
final class ActiveRideModel: ObservableObject {

    @Published private(set) var state = ActiveRideState()

    private let rideId: String
    private let rides: ActiveRideRepository
    private let contact: RideContact
    private let location: DriverLocationSource

    private var projection: RideProjection?
    private var poller: Task<Void, Never>?

    init(rideId: String, rides: ActiveRideRepository, contact: RideContact, location: DriverLocationSource) {
        self.rideId = rideId
        self.rides = rides
        self.contact = contact
        self.location = location
    }

    deinit {
        poller?.cancel()
    }

    /// Subscribes to the driver's own position and starts the poll that stands in for the hub.
    func start() {
        guard poller == nil else { return }

        location.start { [weak self] fix in
            self?.state.position = fix
        }
        // `guard let self` inside the loop, for the reason ``HomeModel/start()`` gives: a loop that
        // only *checks* a weak reference outlives the model it was polling for.
        poller = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: Self.pollNanoseconds)
                guard !Task.isCancelled, let self else { return }
                await self.poll()
            }
        }
    }

    func stop() {
        poller?.cancel()
        poller = nil
        location.stop()
    }

    /// Re-reads the whole aggregate and re-seats the projection on it.
    func refresh() async {
        do {
            let detail = try await rides.detail(rideId: rideId)
            projection = IosRideProjectionKt.rideProjectionOf(detail: detail)
            state.ride = detail
            state.advance(to: RideStateSnapshot(state: detail.state, version: detail.version, offerExpiresAt: nil))
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// The single forward CTA, whatever the ride's state calls it.
    ///
    /// `Accepted` → **I've arrived** · `DriverArrived` → **Start ride** (which raises the OTP entry
    /// rather than sending anything) · `InProgress` → **Complete ride**. One entry point because the
    /// wireframe draws one button, and because which command is legal is a property of the ride rather
    /// than of the tap — `RideProjection.canSend` is what decides, from ADD Appendix B.2's table.
    func advance() async {
        guard let rideState = state.rideState else { return }
        switch rideState {
        case RideState.accepted:
            await send(.markArrived) { try await self.rides.arrive(rideId: self.rideId, version: $0) }

        case RideState.driverArrived:
            open(.startOtp)

        case RideState.inProgress:
            await send(.complete) { try await self.rides.complete(rideId: self.rideId, version: $0) }

        default:
            break
        }
    }

    /// Starts the ride against the rider's four-digit OTP (P-07 for a package's sender OTP).
    ///
    /// `423 otp-locked` once the five attempts are spent; the handoff then needs support, which is
    /// what the resolved copy says.
    func startRide(otp: String) async {
        let kind = state.ride?.kind ?? RideKind.passenger
        open(nil)
        await send(kind == RideKind.package ? .verifyPickupOtp : .start) {
            try await self.rides.start(rideId: self.rideId, kind: kind, version: $0, otp: otp)
        }
    }

    /// **AL-47's confirm** — `POST /v1/fare/pay/driver-qr/confirm` (US-26.1).
    ///
    /// The payment moves to `PaymentState.DriverConfirmedQR`, which is terminal and settles like cash,
    /// so the earning posts (R-05). *"Not received"* raises a dispute instead: a ticket routed Support
    /// → Finance, moving no money because the platform holds none of it.
    func confirmQrPayment(received: Bool) async {
        state.sheet = nil
        state.isBusy = true
        state.errorKey = nil
        do {
            let settled: Bool
            if received {
                settled = try await rides.confirmQrPayment(rideId: rideId) == PaymentState.driverConfirmedQR
            } else {
                // The dispute is the answer too: the ride is off this driver's hands either way, and a
                // Support → Finance ticket is not something they wait on at the kerb.
                try await rides.disputeQrPayment(rideId: rideId, note: nil)
                settled = true
            }

            // No re-read. The ride is still `PaymentPending` until fare-svc's settlement reaches
            // ride-svc through the outbox, and refreshing into that window would raise the sheet again
            // on a driver who has just answered it.
            state.isQrAttested = settled
            state.isFinished = settled
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// AL-48's call-type chooser, and what each answer does.
    ///
    /// **`CallType.freeVoip` leaves this screen** (C093): SCR-DI-031 is where the CallKit call is
    /// reported to the system, the room is joined, the call is timed and — when the media path fails —
    /// *"Call normally instead?"* is offered, so the chooser navigates rather than calling.
    /// `POST /v1/calls/start` is made **there**, once, by the screen that owns the call; making it here
    /// as well would write two `comms.call_log` rows for one tap.
    ///
    /// `CallType.directDial` never leaves: it hands the real number to the platform dialler and records
    /// that it happened. The log is best-effort — a failure to write `comms.call_log` must never stop a
    /// driver reaching the rider — and a dial the system refuses because a call is already up becomes
    /// copy rather than a silent no-op.
    func call(type: CallType) async {
        guard let ride = state.ride else { return }
        open(nil)

        if type == CallType.freeVoip {
            state.isVoipRequested = true
            return
        }

        await contact.startCall(rideId: rideId, kind: ride.kind, type: type)
        guard let phone = ride.counterpartyPhone else { return }
        if !contact.dial(phone) {
            state.errorKey = "ride_call_unavailable"
        }
    }

    /// US-12.8's driver SOS — **opens SCR-DI-032** rather than raising the alarm here (C093).
    ///
    /// The alarm is not a one-tap action: D2' §SCR-DI-032 is a screen with a confirmation, a cancel
    /// window, the emergency contact it will reach and the dispatched state. Raising it from a button
    /// on the sheet as well would be a second, unconfirmed door to the same irrevocable `POST /v1/sos`.
    func openSos() {
        state.isSosRequested = true
    }

    /// Raises one of SCR-DI-015's three sheets, or `nil` to take whichever is up back down.
    func open(_ sheet: RideSheet?) {
        state.sheet = sheet
    }

    /// Clears a consumed navigation request or the last failure.
    func consume() {
        state.isVoipRequested = false
        state.isSosRequested = false
        state.errorKey = nil
    }

    /// Sends one command, guarded by the local table and answered by the server's own state.
    ///
    /// A command the table refuses is not sent: the verdict saves a round trip the driver would spend
    /// fifteen seconds of a ride on. A command the table allows and the server refuses is still the
    /// server's answer — the projection applies it and flags it rather than dropping it.
    private func send(_ command: RideCommand, _ block: (Int32) async throws -> RideStateSnapshot) async {
        guard let version = state.version else { return }
        if let projection, !IosRideProjectionKt.rideProjectionCanSend(projection: projection, command: command) {
            return
        }

        state.isBusy = true
        state.errorKey = nil
        do {
            // The response IS the new state and version — every ride-svc mutation answers with both
            // (D3' §0), so re-reading `GET …/state` afterwards would be a round trip spent learning
            // what the last one already said.
            let moved = try await block(version)
            projection?.onServerState(snapshot: moved)
            state.advance(to: moved)
        } catch {
            state.isBusy = false
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// One tick of the hub stand-in.
    private func poll() async {
        // A package ride has been handed to SCR-DI-016, which polls it itself. Two loops folding
        // server states onto one ride would race each other's `advance(to:)`.
        guard state.isPollable else { return }
        guard let snapshot = try? await rides.snapshot(rideId: rideId) else { return }
        projection?.onServerState(snapshot: snapshot)
        state.advance(to: snapshot)
    }

    /// Five seconds.
    ///
    /// Slow enough to be a fallback rather than a second dispatch plane, fast enough that a passenger
    /// cancelling reaches the driver before they finish parking.
    private static let pollNanoseconds: UInt64 = 5_000_000_000
}
