import Foundation
import MageRideShared

/// SCR-DI-028's data — **two services, because sharing is two halves of one thing**.
///
/// * **registry-svc owns the entitlement.** `POST /v1/vehicles/{id}/share` creates the grant a
///   passenger accepts (US-4.1/4.2, D-22), `GET …/subscribers` is who can currently see the vehicle
///   (US-4.7) and `DELETE …/subscribers/{userId}` takes one away.
/// * **subscription-svc owns the request queue.** A passenger tapping a Mode B marker raises
///   `POST /v1/mode-b/{vehicleId}/access-requests`, and accepting one creates the grant **and starts
///   the subscription** in a single transaction (AL-24, Epic 23). That is why the owner's accept is
///   subscription-svc's and not registry's: registry's `…/share/{grantId}/accept` is the *invited
///   user's* half of an invitation the owner started, which is the opposite direction.
///
/// **Everything here is scoped to one vehicle, and that is the point.** D2' §SCR-DI-028's states:
/// *"the incoming private Mode B subscription requests appear under the particular vehicle they
/// target, never mixed across vehicles"* — which is also all the API offers, since both list reads take
/// a `vehicleId` in the path. There is no cross-vehicle read to accidentally use.
///
/// A protocol for the reason every seam in this target is one: `RegistryApi` and `SubscriptionApi` are
/// Kotlin interfaces and cannot be stood in for from Swift, so a model test with no gateway needs the
/// seam on this side of the bridge.
protocol SharingRepository: AnyObject {

    /// `POST /v1/vehicles/{vehicleId}/share` — offer `userId` visibility of the vehicle (US-4.2).
    ///
    /// The grant is **pending until the passenger accepts it** (US-4.3b), so it does not appear in
    /// ``grantees(vehicleId:)`` straight away and the screen says so rather than showing a row that is
    /// not yet true. `expiresAt` omitted means open-ended; US-4.8's auto-revoke is the server's job.
    func grant(vehicleId: String, userId: String, expiresAt: Timestamp?) async throws -> String

    /// `GET /v1/vehicles/{vehicleId}/subscribers` — the wireframe's *"Current grantees"* (US-4.7).
    func grantees(vehicleId: String) async throws -> [Subscriber]

    /// `DELETE /v1/vehicles/{vehicleId}/subscribers/{userId}` — stop showing this vehicle to them.
    ///
    /// **No screen sends this, on either platform, and that is US-4.7 rather than an omission**: the
    /// story is *"I can **see** a list of all users who currently have access"*, and what ends a grant
    /// is the expiry this screen sets (US-4.8's auto-revoke). It is carried because it is the other
    /// half of the read directly above it and because `registry.yaml`'s other revoke — `DELETE
    /// …/share/{grantId}` — has no id this screen could pass it: `Subscriber` carries a `userId` and no
    /// `grantId`, so the subscriber route is the one that matches the read.
    func revoke(vehicleId: String, userId: String) async throws

    /// `GET /v1/mode-b/{vehicleId}/access-requests` — passengers asking to join (US-4.4).
    func requests(vehicleId: String) async throws -> [AccessRequest]

    /// `POST /v1/mode-b/access-requests/{requestId}/accept` — admit them.
    ///
    /// One call creates the entitlement *and* the subscription; a Paid vehicle's subscription inherits
    /// the vehicle's default monthly fare. Nothing else has to be sent afterwards.
    func accept(requestId: String) async throws

    /// `POST /v1/mode-b/access-requests/{requestId}/reject` — decline.
    ///
    /// The body's `reason` is owner-written and optional, and this screen has nowhere to type one: the
    /// wireframe's row is a bare **Reject** beside **Accept**. Sending an empty body rather than
    /// inventing a reason is the honest call — a rejection with a made-up justification is worse than
    /// one with none.
    func reject(requestId: String) async throws
}

/// ``SharingRepository`` over `:shared`'s typed registry and subscription clients.
final class ApiSharingRepository: SharingRepository {

    private let registry: RegistryApi
    private let subscription: SubscriptionApi

    init(registry: RegistryApi, subscription: SubscriptionApi) {
        self.registry = registry
        self.subscription = subscription
    }

    func grant(vehicleId: String, userId: String, expiresAt: Timestamp?) async throws -> String {
        try await registry.createShareGrant(
            vehicleId: vehicleId,
            request: CreateShareGrantRequest(userId: userId, expiresAt: expiresAt),
            idempotencyKey: nil
        ).grantId
    }

    func grantees(vehicleId: String) async throws -> [Subscriber] {
        try await registry.listVehicleSubscribers(
            vehicleId: vehicleId,
            page: PageRequest.companion.FIRST
        ).items
    }

    func revoke(vehicleId: String, userId: String) async throws {
        try await registry.unsubscribeFromVehicle(vehicleId: vehicleId, userId: userId)
    }

    func requests(vehicleId: String) async throws -> [AccessRequest] {
        try await subscription.listModeBAccessRequests(
            vehicleId: vehicleId,
            page: PageRequest.companion.FIRST
        ).items
    }

    func accept(requestId: String) async throws {
        _ = try await subscription.acceptModeBAccessRequest(requestId: requestId, idempotencyKey: nil)
    }

    func reject(requestId: String) async throws {
        _ = try await subscription.rejectModeBAccessRequest(
            requestId: requestId,
            request: RejectAccessRequest(reason: nil),
            idempotencyKey: nil
        )
    }
}
