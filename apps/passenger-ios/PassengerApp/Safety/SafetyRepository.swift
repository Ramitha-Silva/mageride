import Foundation
import MageRideShared

/// Cluster 8's safety half — safety-svc, as SCR-PI-029 reaches it (US-12.1, D-33, D-34).
///
/// **Two operations, and this is deliberately not on ``RideRepository``.** That door already carries
/// ride-svc, fare-svc, wallet-svc and comms-svc, and its own note says safety-svc is C102's boundary
/// rather than an omission: `POST /v1/sos` having **one** caller is what stops one emergency
/// arriving on the operator's live feed as two events, and the one caller is SCR-PI-029.
/// `apps/passenger-android` made the same move at C084 after C080 had raised the alarm inline from
/// SCR-PA-015; this side starts where that finished, because SCR-PI-015's `⛨ SOS` has navigated
/// since C098.
///
/// **This is not the whole of `safety.yaml`, and the rest is deliberately absent.** The vehicle
/// report (US-12.6), the driver block (D-04/E-07), the SOS history and the public share view all
/// exist on the contract and none has a passenger wireframe cell in this build — a seam over an
/// operation no screen calls is a method nobody maintains. `getSharedTrip` is the **recipient's**
/// read and is `security: []`: it is a browser's, on SCR-WT, and never this app's.
///
/// A Swift protocol rather than `SafetyApi` itself, for the rule `apps/passenger-ios/CLAUDE.md`
/// states: it is a Kotlin interface with `suspend` methods and Swift can stand in for none of them.
/// **No caching and no state** — the screen's state is the screen's.
protocol SafetyRepository: AnyObject {

    /// `POST /v1/sos` — raise the alarm (US-12.1, D-33). Attested (D-30).
    ///
    /// **There is no positionless form**, which is why this takes a coordinate rather than an
    /// optional one: `TriggerSosRequest.lat`/`.lng` are required, so there is no request to make
    /// until the handset has answered once. See ``SosState/isAwaitingPosition``.
    ///
    /// `role` is `SosRole.passenger` and is not a parameter: this is the passenger app, and a client
    /// that could claim to be the driver would put the wrong side of the ride on the operator's feed.
    ///
    /// **The one method in this app's repositories that must throw.** Everything the alarm can do
    /// wrong has to reach the passenger — `400 no-emergency-contact` above all (AL-13), which is a
    /// setup failure SCR-PI-027b can fix.
    func triggerSos(rideId: String, lat: Double, lng: Double) async throws -> SosDispatched

    /// `POST /v1/trip-share/{tripId}` — D-34's live trip link.
    ///
    /// Minted **after** the alarm has gone and allowed to fail; see ``SosModel/mintShareLink()``.
    func shareTrip(rideId: String) async throws -> TripShareLink
}

/// ``SafetyRepository`` over C013's generated safety-svc client.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the Objective-C export.
///
/// Both `idempotencyKey`s are `nil` on purpose, and for opposite reasons. R-18's dedupe key belongs
/// to an operation a *retry* must not repeat — but an alarm is not retried by the client at all
/// (``SosModel/raise()`` refuses a second tap once one is in flight), and a genuinely repeated
/// alarm from a passenger who pressed the disc twice minutes apart is **two** events an operator
/// should see. The share link is idempotent server-side on the trip: `POST /v1/trip-share/{tripId}`
/// answers the ride's own token.
final class ApiSafetyRepository: SafetyRepository {

    private let safety: SafetyApi

    init(safety: SafetyApi) {
        self.safety = safety
    }

    func triggerSos(rideId: String, lat: Double, lng: Double) async throws -> SosDispatched {
        try await safety.triggerSos(
            request: TriggerSosRequest(rideId: rideId, lat: lat, lng: lng, role: SosRole.passenger),
            idempotencyKey: nil
        )
    }

    func shareTrip(rideId: String) async throws -> TripShareLink {
        try await safety.createTripShare(tripId: rideId, idempotencyKey: nil)
    }
}
