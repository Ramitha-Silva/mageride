import CallKit
import Foundation
import MageRideShared
import UIKit

/// Reaching the other party — SCR-DI-015's **Call** button, and the log behind it.
///
/// **AL-48 — there is no masking bridge left.** The call-type chooser keeps two options and only two:
/// **Free call** mints a WebRTC session, and **Normal call** is a *direct cellular dial* of the
/// counterparty's real number, which `RideDetail.counterpartyPhone` carries from `Accepted` onward.
/// The masked-number PSTN bridge and D-25's masked-SMS relay were both withdrawn; a VoIP failure
/// falls back to *"Call normally instead?"* — the same direct dial (US-26.4, BR-30.3).
///
/// **P-05 — the driver calls the rider, never the booker.** On a proxy booking the person in the car
/// is not the person who paid, and `counterpartyPhone` is already the rider's; ``CalleeRole`` is what
/// says so on the log.
///
/// **CallKit is SCR-DI-031's, not this screen's** (D2' §C's "WebRTC + **ConnectionService** /
/// **CallKit**"). A free call *leaves* SCR-DI-015 — `POST /v1/calls/start` mints the room there, once,
/// and `CXProvider` reports it to the system there — because making the call here as well would write
/// two `comms.call_log` rows for one tap. What is CallKit-aware on *this* screen is the direct dial:
/// it goes through ``dial(_:)``, which refuses while the system already has a call up rather than
/// throwing the driver out of a conversation they are having with the same rider.
@MainActor
protocol RideContact: AnyObject {

    /// `POST /v1/calls/start` — records the call and, for `CallType.freeVoip`, mints the session.
    ///
    /// A `CallType.directDial` call creates no session at all: the response is a `comms.call_log` row
    /// and nothing else, because a `tel:` dial cannot be server-verified. The log is **best-effort**
    /// for the same reason — a failure here must never stop the dial.
    func startCall(rideId: String, kind: RideKind, type: CallType) async

    /// The same call, with the role **named** rather than derived (AL-33, Δ C089).
    ///
    /// SCR-DI-016a/c put a button beside *both* ends of a delivery, so the ride's kind cannot decide who
    /// is being rung — the driver's tap is what decides, and the log has to record which. The kind-based
    /// form above is the passenger screen's and stays, because there the answer really is a property of
    /// the ride.
    func startCall(rideId: String, calleeRole: CalleeRole, type: CallType) async

    /// Hands [phone] to the platform dialler.
    ///
    /// - Returns: `false` when the handset has no telephony or a call is already up — the caller
    ///   renders copy rather than leaving the driver tapping a button that does nothing.
    @discardableResult
    func dial(_ phone: String) -> Bool
}

/// ``RideContact`` over voip-svc and the system dialler.
@MainActor
final class SystemRideContact: RideContact {

    private let voip: VoipApi

    init(voip: VoipApi) {
        self.voip = voip
    }

    func startCall(rideId: String, kind: RideKind, type: CallType) async {
        await startCall(rideId: rideId, calleeRole: Self.calleeRole(kind), type: type)
    }

    func startCall(rideId: String, calleeRole: CalleeRole, type: CallType) async {
        // Best-effort, and silently. A driver reaching the rider must never depend on
        // `comms.call_log` being writable, which is the same rule the Android twin states.
        _ = try? await voip.startCall(
            request: StartCallRequest(rideId: rideId, calleeRole: calleeRole, callType: type),
            idempotencyKey: nil
        )
    }

    /// **CallKit-aware.** `CXCallObserver` is the only way to know whether the system already has a
    /// call — an in-app VoIP call this app placed through SCR-DI-031, or an ordinary cellular one —
    /// and dialling over one hangs it up. On Android there is nothing to check, because
    /// `ACTION_DIAL` opens the dialler rather than placing the call; on iOS a `tel:` URL **does**
    /// place it, so the check is the platform difference and not a nicety.
    @discardableResult
    func dial(_ phone: String) -> Bool {
        guard callObserver.calls.isEmpty else { return false }
        guard let url = URL(string: "tel:" + phone.filter { $0.isNumber || $0 == "+" }),
              UIApplication.shared.canOpenURL(url)
        else { return false }

        UIApplication.shared.open(url)
        return true
    }

    /// Held rather than constructed per dial: `CXCallObserver` populates `calls` asynchronously after
    /// it is created, so a fresh one always answers "no calls" and the check would never fire.
    private let callObserver = CallObserver()

    /// Who the driver is calling, when only the ride's kind says so.
    ///
    /// A package has no rider — the person at the pickup is the **sender**, and the delivery sheets
    /// (SCR-DI-016a/b/c) name the role outright through the overload above, because they can reach the
    /// recipient too.
    private static func calleeRole(_ kind: RideKind) -> CalleeRole {
        kind == RideKind.package ? CalleeRole.sender : CalleeRole.passenger
    }
}

/// Whether the system already has a call up.
///
/// A held `CXCallObserver` rather than one constructed per dial: the observer populates `calls`
/// asynchronously after it is created, so a fresh one always answers "none" and the guard would never
/// fire. It needs no delegate — `calls` is a synchronous read of the current state, which is the whole
/// question ``SystemRideContact/dial(_:)`` asks.
@MainActor
final class CallObserver {

    private let observer = CXCallObserver()

    /// The calls CallKit knows about — this app's SCR-DI-031 session and any cellular call.
    var calls: [CXCall] { observer.calls }
}
