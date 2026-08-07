import Foundation
import MageRideShared

/// **SCR-DI-016a/b/c · the three delivery sheets** (AL-33, US-20.12/20.13).
///
/// One model for all three, because they are one job: *review → pick up → hand over*, over the package
/// ride's own state machine. Which sheet is up is **derived from the ride**, not held as a step counter
/// — `package.picked_up` is the `→ InProgress` move, so a driver whose app died between the two doors
/// comes back to the right sheet by reading the ride rather than by remembering anything (P-07).
///
/// **The attempt budget is `:shared`'s, not this screen's.** `PackageHandoff` (C015) is the P-07 rule as
/// a type — five attempts per gate, the sixth `423 otp-locked` and the handoff goes to the admin queue —
/// and `RideProjection` already owns one per package ride. Counting here as well would be a second
/// answer to a question the domain layer has, and the two would eventually disagree. The projection is
/// therefore **seated once and kept**, unlike ``ActiveRideModel``'s, which re-seats on every read:
/// re-seating throws the handoff away and with it the count of how many codes the driver has got wrong.
///
/// **Both call buttons are a direct PSTN dial** and there is no chooser: AL-33 says the delivery sheets
/// place *"a real mobile-phone voice call"* to whichever end of the delivery was tapped, which is a
/// different decision from a ride's AL-48 Free-call/Normal-call pair. The log still goes through
/// `POST /v1/calls/start`, and still best-effort — a failure to write `comms.call_log` must never stop a
/// courier reaching a doorstep. The dial itself is still CallKit-gated (`SystemRideContact.dial`), which
/// is this platform's own delta and not a second rule.
@MainActor
final class DeliveryModel: ObservableObject {

    @Published private(set) var state: DeliveryState

    private let rideId: String
    private let deliveries: DeliveryRepository
    private let contact: RideContact
    private let location: DriverLocationSource
    private let proofs: ProofUploadQueue
    private let captures: DocumentCaptureCoordinator

    /// Seated on the first read and kept for the screen's life. See the type's own note.
    private var projection: RideProjection?
    private var poller: Task<Void, Never>?

    init(
        rideId: String,
        deliveries: DeliveryRepository,
        contact: RideContact,
        location: DriverLocationSource,
        proofs: ProofUploadQueue,
        captures: DocumentCaptureCoordinator
    ) {
        self.rideId = rideId
        self.deliveries = deliveries
        self.contact = contact
        self.location = location
        self.proofs = proofs
        self.captures = captures
        // A photograph taken before the sheet was rebuilt is still this delivery's: SCR-DI-005 is a
        // full-screen takeover and the queue is the process singleton that outlives it.
        state = DeliveryState(proof: proofs.pending(for: rideId))
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
        // `guard let self` inside the loop, for the reason ``ActiveRideModel/start()`` gives: a loop
        // that only *checks* a weak reference outlives the model it was polling for.
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

    /// Re-reads the aggregate and folds it onto the sheets.
    func refresh() async {
        do {
            let detail = try await deliveries.detail(rideId: rideId)
            if let projection {
                projection.onServerState(detail: detail)
            } else {
                projection = IosRideProjectionKt.rideProjectionOf(detail: detail)
            }
            state.apply(detail)
            state.gates = handoffState
        } catch {
            state.isBusy = false
            state.errorKey = DeliveryModel.messageKey(for: error)
        }
    }

    /// The sheet's own forward action — one button per sheet, three different things.
    ///
    /// **Sheet 1 · Start delivery is local, and deliberately so.** Nothing on the wire corresponds to a
    /// driver setting off: the parcel is released by the sender's OTP, and `POST …/arrive` is the
    /// passenger flow's geofence marker, which D5' §11 skips outright by taking the package straight
    /// from `Accepted` to `InProgress`. Sending anything here would be inventing a transition to make a
    /// button feel busy.
    ///
    /// **Sheet 2 · Verify pickup** spends the sender's code. **Sheet 3 · Delivery completed** spends the
    /// recipient's, or uploads the photograph that stands in for it (P-10).
    func advance() async {
        guard state.canAdvance else { return }
        switch state.sheet {
        case .review:
            state.isStarted = true
            state.errorKey = nil

        case .pickup:
            await verify(PackageGate.pickup)

        case .complete:
            await complete()
        }
    }

    /// **Cancel on sheet 1** — the driver puts the delivery down before setting off.
    ///
    /// It does not cancel the *delivery*: AL-33 releases the request back to dispatch for the next
    /// eligible driver. What a client can do is release it from this driver, which is what this sends;
    /// see ``DeliveryRepository/release(rideId:version:)`` for why the re-dispatch half does not happen
    /// yet.
    func cancel() async {
        guard let version = state.version else { return }
        state.isBusy = true
        state.errorKey = nil
        do {
            try await deliveries.release(rideId: rideId, version: version)
            proofs.discard(rideId: rideId)
            state.isBusy = false
            state.isFinished = true
        } catch {
            state.isBusy = false
            state.errorKey = DeliveryModel.messageKey(for: error)
        }
    }

    /// A digit was typed. Non-digits and anything past the fourth are dropped by the field.
    func onOtpChange(_ otp: String) {
        state.otp = otp
        state.errorKey = nil
    }

    /// The 📷 **Photo proof** button. The scanner is SCR-DI-005's; this only says what it is for.
    func requestProofCapture() {
        captures.open(.deliveryProof)
    }

    /// Files the photograph SCR-DI-005 handed back (P-10).
    ///
    /// It is **queued, not sent**: the upload is what completes the delivery, so it belongs to the
    /// *"Delivery completed"* tap and not to the shutter. The driver can retake before committing.
    ///
    /// A result this screen did not ask for is left alone rather than consumed out from under the screen
    /// that did — the same rule `VehicleOnboardingModel.apply(_:)` states.
    func apply(_ result: DocumentCaptureResult) {
        guard result.target == .deliveryProof else { return }
        state.proof = proofs.enqueue(
            rideId: rideId,
            image: result.image,
            at: state.position?.point,
            capturedAt: Date()
        )
        state.errorKey = nil
        captures.consume()
    }

    /// A call button — **a direct cellular dial of that party's real number** (AL-33).
    ///
    /// Distinct from a ride's masked-free VoIP choice: a courier needs the person at the door, and the
    /// number is the one `RideDetail` carries from `Accepted` onward. The `comms.call_log` write is
    /// best-effort, so the dial is attempted whether or not the log call succeeds; a dial the system
    /// refuses because a call is already up becomes copy rather than a silent no-op.
    func call(_ party: DeliveryParty) async {
        guard let phone = state.phone(of: party) else { return }
        await contact.startCall(rideId: rideId, calleeRole: party.calleeRole, type: CallType.directDial)
        if !contact.dial(phone) {
            state.errorKey = "ride_call_unavailable"
        }
    }

    /// US-12.8's driver SOS — **opens SCR-DI-032**, the same screen SCR-DI-015's button opens (C093).
    ///
    /// The alarm is not a one-tap action: D2' §SCR-DI-032 is a screen with a confirmation, a cancel
    /// window, the emergency contact it will reach and the dispatched state. Raising it from a button on
    /// the sheet as well would be a second, unconfirmed door to the same irrevocable `POST /v1/sos`.
    func openSos() {
        state.isSosRequested = true
    }

    /// Clears a consumed navigation request or the last failure.
    func consume() {
        state.isSosRequested = false
        state.errorKey = nil
    }

    // MARK: -

    /// Spends one OTP attempt against [gate].
    ///
    /// The verdict is `:shared`'s: `canSubmit` refuses a malformed entry **without spending an
    /// attempt** — the budget exists to stop guessing, and a typo is not a guess — and the outcome of a
    /// rejection is what decides between *"wrong code, N left"* and the admin queue.
    private func verify(_ gate: PackageGate) async {
        guard let handoff = projection?.handoff else { return }
        let otp = state.otp
        guard handoff.canSubmit(gate: gate, otp: otp) else { return }

        state.isBusy = true
        state.errorKey = nil
        do {
            // An `if` rather than a ternary: Swift refuses `try` to the right of a non-assignment
            // operator, and both arms of this one are a `try await`.
            let moved: RideStateSnapshot
            if gate == PackageGate.pickup {
                moved = try await deliveries.verifyPickup(rideId: rideId, otp: otp)
            } else {
                moved = try await deliveries.verifyDelivery(rideId: rideId, otp: otp)
            }
            handoff.onVerified(gate: gate)
            projection?.onServerState(snapshot: moved)
            state.advance(to: moved, gates: handoffState)
        } catch {
            onOtpFailure(gate, error)
        }
    }

    /// Sheet 3's CTA — *"Delivery completed"*, which **replaces** the old *"Cash received (COD)"*.
    ///
    /// A photograph in hand takes the P-10 route and is itself the completion (Δ C037); otherwise the
    /// recipient's OTP is. Neither settles the cash: AL-33 decoupled that, and uncollected COD is a
    /// 24-hour timer's problem rather than this button's (P-14).
    private func complete() async {
        guard let queued = state.proof else {
            await verify(PackageGate.delivery)
            return
        }

        state.isBusy = true
        state.errorKey = nil
        do {
            let claimed = proofs.claim(queued.id) ?? queued
            let stored = try await deliveries.deliverWithProof(rideId: rideId, proof: claimed)
            proofs.markUploaded(claimed.id)
            projection?.handoff?.onProofPhotoStored()
            state.proof = nil

            // Δ C037 reports where the ride landed, so there is no re-read. A server that answers the
            // older shape leaves it absent and the five-second poll picks the move up instead; the
            // version then stays the one the screen was holding, because a number invented here would
            // be echoed on the next mutation as if it were the server's (R-14).
            guard let landed = stored.state, let version = stored.version?.int32Value ?? state.version else {
                state.isBusy = false
                return
            }
            let snapshot = RideStateSnapshot(state: landed, version: version, offerExpiresAt: nil)
            projection?.onServerState(snapshot: snapshot)
            state.advance(to: snapshot, gates: handoffState)
        } catch {
            // The photograph goes back in the queue rather than being dropped: the driver is still at
            // the door, and the retry must not need the camera again.
            proofs.reschedule(queued.id)
            state.proof = proofs.pending(for: rideId) ?? queued
            state.isBusy = false
            state.errorKey = DeliveryModel.messageKey(for: error)
        }
    }

    /// A refused OTP, told apart by its code (D-26 — never by the server's English message).
    ///
    /// `423 otp-locked` is the server saying the budget is gone, which is authoritative over the local
    /// count: a driver resuming on a second handset starts at zero attempts here and at five there.
    /// Anything else that is not an `invalid-otp` is an ordinary failure and costs no attempt.
    ///
    /// One write, not two. Two would put a frame on screen showing a locked gate with no explanation
    /// under it, which is the state a driver would be looking at when they decide the sheet is broken.
    private func onOtpFailure(_ gate: PackageGate, _ error: Error) {
        let handoff = projection?.handoff
        if let failure = DeliveryModel.kotlinError(error) {
            if failure is MageRideErrorLocked || failure.code == ErrorCode.otpLocked {
                handoff?.onServerLocked(gate: gate)
            } else if failure.code == ErrorCode.invalidOtp {
                handoff?.onRejected(gate: gate)
            }
        }

        var next = state
        next.isBusy = false
        next.otp = ""
        next.gates = handoffState ?? next.gates
        next.errorKey = DeliveryModel.messageKey(for: error)
        state = next
    }

    /// One tick of the hub stand-in.
    private func poll() async {
        let current = state
        guard !current.isFinished, !current.isBusy, current.ride != nil else { return }
        guard let snapshot = try? await deliveries.snapshot(rideId: rideId) else { return }
        projection?.onServerState(snapshot: snapshot)
        state.advance(to: snapshot, gates: handoffState)
    }

    /// The handoff's two gates as a value, or `nil` before the projection is seated.
    ///
    /// `StateFlow.value` erases to `Any?` across the bridge, so the cast is the read — the same shape
    /// `SharedDriverSessions` uses for `SessionState`.
    private var handoffState: PackageHandoffState? {
        projection?.handoff?.state.value as? PackageHandoffState
    }

    /// The trilingual copy for a failure — D-26's rule, plus the two these sheets name themselves.
    private static func messageKey(for error: Error) -> String {
        guard let failure = kotlinError(error) else { return OnboardingErrors.messageKey(for: error) }
        if failure is MageRideErrorLocked || failure.code == ErrorCode.otpLocked { return "delivery_otp_locked" }
        if failure.code == ErrorCode.invalidOtp { return "delivery_otp_wrong" }
        return OnboardingErrors.messageKey(for: error)
    }

    /// The `MageRideError` behind a Swift `Error`, unwrapped from the `NSError` Kotlin/Native wraps it
    /// in. See ``OnboardingErrors/kotlinCause(of:)`` for why the unwrap is necessary at all.
    private static func kotlinError(_ error: Error) -> MageRideError? {
        OnboardingErrors.kotlinCause(of: error) as? MageRideError
    }

    /// Five seconds — SCR-DI-015's interval, for the same reason and the same documented fallback.
    private static let pollNanoseconds: UInt64 = 5_000_000_000
}
