import Foundation
import MageRideShared

/// Which of AL-33's three sheets is up.
///
/// They are **sequential, not switchable**: a delivery moves forward through them and never back,
/// because each step is a thing that has already happened to the parcel.
enum DeliverySheet {

    /// SCR-DI-016a — the job in front of the driver, before they set off.
    case review

    /// SCR-DI-016b — at the sender's door, entering the pickup code.
    case pickup

    /// SCR-DI-016c — at the recipient's door, entering the delivery code or photographing it.
    case complete
}

/// Which end of the delivery a call button reaches.
enum DeliveryParty: CaseIterable {

    /// The account that booked the delivery, and the person holding the parcel at the start.
    case sender

    /// The person the parcel is for.
    case recipient

    /// Who the driver is calling, as `comms.call_log` records it (AL-33).
    var calleeRole: CalleeRole {
        switch self {
        case .sender: return CalleeRole.sender
        case .recipient: return CalleeRole.recipient
        }
    }

    /// The role's own name — the wireframe's `Sender` / `Recipient`.
    var labelKey: String {
        switch self {
        case .sender: return "delivery_party_sender"
        case .recipient: return "delivery_party_recipient"
        }
    }
}

/// SCR-DI-016a/b/c's state.
///
/// - Parameters:
///   - ride: The aggregate, as ride-svc last answered a **full** read of it.
///   - moved: The latest state and version, whether from the full read, a command's response or a
///     poll. Held **beside** ``ride`` for ``ActiveRideState``'s reason: `RideDetail` is a Kotlin data
///     class whose `copy` reaches Swift as a twenty-two-argument `doCopy`, and the state and the
///     version are the only two things that move between full reads. That is the one shape difference
///     from `apps/driver-android`'s `DeliveryState`, which folds them back into the aggregate.
///   - position: The driver's own last fix — the map marker and the `captured_geo` stamped on a proof
///     photo.
///   - gates: `:shared`'s `PackageHandoff` state — the two OTP budgets and the proof flag. `nil` until
///     the first read seats the projection.
///   - isStarted: The driver tapped **Start delivery** on sheet 1. Local, because nothing on the wire
///     corresponds to it — see ``DeliveryModel/advance()``.
///   - otp: What has been typed into the sheet's four boxes.
///   - proof: A photograph queued against this delivery (P-10), if one has been taken.
///   - isSosRequested: The driver tapped **SOS**; the screen opens SCR-DI-032 (C093).
///   - isFinished: The delivery is off this driver's hands — handed over, or released.
///   - isBusy: A command is in flight.
///   - errorKey: Resolved copy for the last failure.
struct DeliveryState {

    var ride: RideDetail?
    var moved: RideStateSnapshot?
    var position: Fix?
    var gates: PackageHandoffState?
    var isStarted = false
    var otp = ""
    var proof: ProofUpload?
    var isSosRequested = false
    var isFinished = false
    var isBusy = false
    var errorKey: String?

    /// Where the ride is, or `nil` before the first read.
    var rideState: RideState? { moved?.state ?? ride?.state }

    /// The version the next command must echo (R-14) — the newest one seen, from whichever read.
    var version: Int32? { moved?.version ?? ride?.version }

    /// Whether the parcel is aboard.
    ///
    /// `package.picked_up` is the `→ InProgress` move (P-07), so the ride's own state is the answer —
    /// not the local ``isStarted`` flag, which only says the driver has set off.
    var isPickedUp: Bool { rideState == RideState.inprogress || isHandedOver }

    /// Whether the parcel has been handed over.
    ///
    /// `Completed` onward, and **not** `isTerminal` alone: AL-33 decouples the cash from the handover,
    /// so a COD delivery sits at `PaymentPending` with nothing left for the driver to do. Waiting for a
    /// terminal ride state would hold a courier on a doorstep for a reconciliation that happens
    /// somewhere else entirely (P-14).
    ///
    /// Unwrapped before the comparison rather than matched as an optional, for the reason every switch
    /// over a shared enum in this target gives: a Kotlin enum reaches Swift as a **class**.
    var isHandedOver: Bool {
        guard let rideState else { return false }
        return rideState == RideState.completed || rideState == RideState.paymentpending || rideState.isTerminal
    }

    /// Which sheet the delivery has reached.
    var sheet: DeliverySheet {
        if isPickedUp { return .complete }
        return isStarted ? .pickup : .review
    }

    /// The OTP gate this sheet is asking about, or `nil` on the review sheet.
    var gate: PackageGate? {
        switch sheet {
        case .review: return nil
        case .pickup: return PackageGate.pickup
        case .complete: return PackageGate.delivery
        }
    }

    /// What the current gate's sheet should render (P-07).
    var outcome: PackageGateOutcome? {
        guard let gate, let gates else { return nil }
        return gates.outcomeOf(gate: gate)
    }

    /// Whether five attempts are spent and the handoff is with the admin queue.
    ///
    /// The server's `423 otp-locked` is what sets it; the local count is a mirror that a second device
    /// would not have, which is exactly why `PackageGateState` keeps the two apart.
    var isLocked: Bool {
        guard let outcome else { return false }
        return outcome is PackageGateOutcomeAdminQueue
    }

    /// Attempts left on the current gate, for the counter under the boxes.
    var attemptsRemaining: Int {
        Int(gateState?.attemptsRemaining ?? PackageHandoff.companion.MAX_OTP_ATTEMPTS)
    }

    /// Rejected attempts on the current gate — the wireframe's *"attempt N of 5"*.
    var attemptsUsed: Int { Int(gateState?.attemptsUsed ?? 0) }

    /// Whether the sheet's forward action can be tapped.
    var canAdvance: Bool {
        guard !isBusy else { return false }
        switch sheet {
        case .review:
            return ride != nil

        case .pickup:
            return !isLocked && PackageHandoff.companion.isWellFormed(otp: otp)

        // P-10: a photograph completes the delivery on its own when nobody is there to read the code
        // out, so sheet 3's CTA is live with either proof in hand.
        case .complete:
            return proof != nil || (!isLocked && PackageHandoff.companion.isWellFormed(otp: otp))
        }
    }

    /// How far the driver is from the pickup — the wireframe's `Pickup · 1.2 km`.
    var pickupMetres: Double? {
        guard let position, let pickup = ride?.pickup else { return nil }
        return GeoDistanceKt.distanceMetres(from: position.point, to: pickup.point)
    }

    /// How far the parcel has to travel — the wireframe's `Drop · 4.6 km`.
    ///
    /// Pickup to drop, not driver to drop: it is the length of the delivery, which is what a driver
    /// deciding whether to take the job is reading.
    var dropMetres: Double? {
        guard let ride else { return nil }
        return GeoDistanceKt.distanceMetres(from: ride.pickup.point, to: ride.dropoff.point)
    }

    /// MAP-10's 100 m circle — the pickup until the parcel is aboard, the drop afterwards.
    var geofence: GeoPoint? {
        guard let ride else { return nil }
        return isPickedUp ? ride.dropoff.point : ride.pickup.point
    }

    /// The number behind a call button, or `nil` when the ride does not carry it.
    func phone(of party: DeliveryParty) -> String? {
        switch party {
        case .sender:
            return ride?.senderPhone

        // `counterpartyPhone` is the recipient on a package ride (Δ C037), and is the fallback for a
        // server that answers the older shape.
        case .recipient:
            return ride?.recipientPhone ?? ride?.counterpartyPhone
        }
    }

    /// `Sender` / `Recipient · Sunethra`.
    ///
    /// **There is no sender name on the wire.** `RideDetail` gained `recipientName` in Δ C037 and no
    /// counterpart for the account that booked the delivery, so the sender's row is its role alone
    /// rather than a name borrowed from a field that means something else. Recorded as a spec gap in
    /// the C071 handoff and carried forward.
    func label(of party: DeliveryParty) -> String {
        let role = party.labelKey.localised
        guard party == .recipient, let name = ride?.recipientName, !name.isEmpty else { return role }
        return role + MageRideSymbols.separator + name
    }

    /// Folds a full read onto the sheets.
    ///
    /// The **local** ``isStarted`` flag is raised by a ride that has moved past the review: a driver
    /// resuming a delivery already `InProgress` must not be shown sheet 1 again, and one at
    /// `DriverArrived` has plainly set off.
    mutating func apply(_ detail: RideDetail) {
        ride = detail
        moved = RideStateSnapshot(state: detail.state, version: detail.version, offerExpiresAt: nil)
        isBusy = false
        isStarted = isStarted || detail.state != RideState.accepted
        isFinished = isFinished || isHandedOver
    }

    /// Folds a server-confirmed state change on.
    ///
    /// ``isFinished`` is sticky for SCR-DI-015's reason: a delivery that has been handed over never
    /// un-hands-over, and a late poll landing after the driver has been sent back to standby must not
    /// undo that.
    ///
    /// **The boxes are cleared only when the ride actually moved** — Δ C089. The Android twin clears
    /// them on every fold, and the five-second poll is a fold, so a courier typing the recipient's code
    /// there watches it disappear under them. Clearing on a *transition* is what the rule is for (the
    /// sender's code is spent and the recipient's is a different one), and it keeps C071's own
    /// assertion — *"the boxes are cleared for the recipient's code"* — true. Recorded as a defect
    /// found in C071.
    mutating func advance(to snapshot: RideStateSnapshot, gates: PackageHandoffState?) {
        if rideState != snapshot.state { otp = "" }
        isBusy = false
        moved = snapshot
        if let gates { self.gates = gates }
        isStarted = isStarted || isPickedUp
        isFinished = isFinished || isHandedOver
    }

    /// The raw gate state, or `nil` on the review sheet and before the first read.
    private var gateState: PackageGateState? {
        guard let gate, let gates else { return nil }
        return gates.stateOf(gate: gate)
    }
}
