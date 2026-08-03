package lk.mageride.driver.sharing

import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.CreateShareGrantRequest
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.RejectAccessRequest

/**
 * SCR-DA-028's data — **two services, because sharing is two halves of one thing**.
 *
 * * **registry-svc owns the entitlement.** `POST /v1/vehicles/{id}/share` creates the grant a
 *   passenger accepts (US-4.1/4.2, D-22), `GET …/subscribers` is who can currently see the vehicle
 *   (US-4.7) and `DELETE …/subscribers/{userId}` takes one away.
 * * **subscription-svc owns the request queue.** A passenger tapping a Mode B marker raises
 *   `POST /v1/mode-b/{vehicleId}/access-requests`, and accepting one creates the grant **and starts
 *   the subscription** in a single transaction (AL-24, Epic 23). That is why the owner's accept is
 *   subscription-svc's and not registry's: registry's `…/share/{grantId}/accept` is the *invited
 *   user's* half of an invitation the owner started, which is the opposite direction.
 *
 * **Everything here is scoped to one vehicle, and that is the point.** D2' §SCR-DA-028's states:
 * *"the incoming private Mode B subscription requests appear under the particular vehicle they
 * target, never mixed across vehicles"* — which is also all the API offers, since both list reads
 * take a `vehicleId` in the path. There is no cross-vehicle read to accidentally use.
 */
internal class SharingRepository(private val registry: RegistryApi, private val subscription: SubscriptionApi) {

    /**
     * `POST /v1/vehicles/{vehicleId}/share` — offer [userId] visibility of the vehicle (US-4.2).
     *
     * The grant is **pending until the passenger accepts it** (US-4.3b), so it does not appear in
     * [grantees] straight away and the screen says so rather than showing a row that is not yet
     * true. [expiresAt] omitted means open-ended; US-4.8's auto-revoke is the server's job.
     */
    suspend fun grant(vehicleId: Ulid, userId: Ulid, expiresAt: Timestamp?): Ulid =
        registry.createShareGrant(vehicleId, CreateShareGrantRequest(userId = userId, expiresAt = expiresAt)).grantId

    /** `GET /v1/vehicles/{vehicleId}/subscribers` — the wireframe's *"Current grantees"* (US-4.7). */
    suspend fun grantees(vehicleId: Ulid): List<Subscriber> =
        registry.listVehicleSubscribers(vehicleId, PageRequest.FIRST).items

    /**
     * `DELETE /v1/vehicles/{vehicleId}/subscribers/{userId}` — stop showing this vehicle to them.
     *
     * By **user**, not by grant: `Subscriber` carries a `userId` and no `grantId`, so
     * `DELETE …/share/{grantId}` — the other revoke `registry.yaml` declares — has no id this
     * screen could pass it. The subscriber route is the one that matches the read.
     */
    suspend fun revoke(vehicleId: Ulid, userId: Ulid) {
        registry.unsubscribeFromVehicle(vehicleId, userId)
    }

    /** `GET /v1/mode-b/{vehicleId}/access-requests` — passengers asking to join (US-4.4). */
    suspend fun requests(vehicleId: Ulid): List<AccessRequest> =
        subscription.listModeBAccessRequests(vehicleId, PageRequest.FIRST).items

    /**
     * `POST /v1/mode-b/access-requests/{requestId}/accept` — admit them.
     *
     * One call creates the entitlement *and* the subscription; a Paid vehicle's subscription
     * inherits the vehicle's default monthly fare. Nothing else has to be sent afterwards.
     */
    suspend fun accept(requestId: Ulid) {
        subscription.acceptModeBAccessRequest(requestId)
    }

    /**
     * `POST /v1/mode-b/access-requests/{requestId}/reject` — decline.
     *
     * The body's `reason` is owner-written and optional, and this screen has nowhere to type one:
     * the wireframe's row is a bare **Reject** beside **Accept**. Sending an empty body rather than
     * inventing a reason is the honest call — a rejection with a made-up justification is worse
     * than one with none.
     */
    suspend fun reject(requestId: Ulid) {
        subscription.rejectModeBAccessRequest(requestId, RejectAccessRequest())
    }
}
