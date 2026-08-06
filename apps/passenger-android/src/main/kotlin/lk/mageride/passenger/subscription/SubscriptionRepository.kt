package lk.mageride.passenger.subscription

import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.ModeBFileKind
import lk.mageride.shared.data.models.subscription.PayModeBSubscriptionRequest
import lk.mageride.shared.data.models.subscription.RequestModeBAccessRequest
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment

/**
 * Cluster 6's seam — Mode B as a **passenger** touches it.
 *
 * `subscription.yaml` is one contract carrying two unrelated money flows: the driver's daily
 * platform fee and reseller credit, and this — a passenger subscribing to a private vehicle and
 * paying its owner monthly. Only the second half is behind this interface, and the omissions are
 * the point:
 *
 * - **The roster operations are the OWNER's.** `listModeBSubscribers`, `setSubscriberFare`,
 *   `markSubscriberCashPaid`, `deleteModeBSubscriber` and `confirmTransferSlip` all answer
 *   `403 not-owner` to a passenger bearer, and the Fleet Portal is where they are called. A
 *   passenger app that carried them would be a screen away from asking.
 * - **Accept and reject are the driver's/owner's** (SCR-DA-028, SCR-FP-011). This side only
 *   raises the request; AL-25's *"re-joining requires request → accept"* is a rule about two
 *   surfaces, and this is the one that asks.
 *
 * **Money here never touches the platform ledger** (AL-24, §18b). A subscription payment goes to
 * the fleet owner's own account — `payTo`, served only from a `verified` payout profile (AL-49) —
 * and MageRide holds none of it. That is why nothing in this cluster reaches wallet-svc.
 */
internal interface SubscriptionRepository {

    /** `POST /v1/mode-b/{vehicleId}/access-requests` — ask this vehicle's owner for access. */
    suspend fun requestAccess(vehicleId: Ulid, note: String? = null, idempotencyKey: String? = null): AccessRequest

    /** `GET /v1/mode-b/subscriptions/{passengerId}` — SCR-PA-025's cards. */
    suspend fun subscriptions(passengerId: Ulid, page: PageRequest = PageRequest.FIRST): Page<Subscription>

    /** `POST /v1/mode-b/subscriptions/{id}/unsubscribe` — leave, and lose sight of the vehicle. */
    suspend fun unsubscribe(subscriptionId: Ulid, idempotencyKey: String? = null): Subscription

    /** `POST /v1/mode-b/subscriptions/{id}/pay` — initiate a month and learn where to send it. */
    suspend fun pay(
        subscriptionId: Ulid,
        method: SubscriptionPayMethod,
        idempotencyKey: String? = null,
    ): SubscriptionPayment

    /** `POST /v1/mode-b/payments/{paymentId}/transfer-slip` — the screenshot US-23.4 requires. */
    suspend fun uploadSlip(paymentId: Ulid, slip: FileUpload, idempotencyKey: String? = null): SubscriptionPayment

    /** `GET /v1/mode-b/subscriptions/{id}/payments` — SCR-PA-025b's statement. */
    suspend fun payments(subscriptionId: Ulid, page: PageRequest = PageRequest.FIRST): Page<SubscriptionPayment>

    /**
     * The owner's bank-app LankaQR image, as bytes (AL-49).
     *
     * Takes the signed link `payTo.lankaqrImageUrl` carries and answers the image behind it, or
     * `null` when the link is not one this build understands. See [SignedFileLink] for why the
     * app re-derives the call rather than handing the URL to a loader.
     */
    suspend fun ownerLankaQr(link: String): ByteArray?
}

/** [SubscriptionRepository] over C013's generated `subscription-svc` client. */
internal class ApiSubscriptionRepository(private val subscriptions: SubscriptionApi) : SubscriptionRepository {

    override suspend fun requestAccess(vehicleId: Ulid, note: String?, idempotencyKey: String?): AccessRequest =
        subscriptions.requestModeBAccess(
            vehicleId = vehicleId,
            request = RequestModeBAccessRequest(note = note?.takeIf(String::isNotBlank)),
            idempotencyKey = idempotencyKey,
        )

    override suspend fun subscriptions(passengerId: Ulid, page: PageRequest): Page<Subscription> =
        subscriptions.listPassengerSubscriptions(passengerId, page)

    override suspend fun unsubscribe(subscriptionId: Ulid, idempotencyKey: String?): Subscription =
        subscriptions.unsubscribeModeB(subscriptionId, idempotencyKey)

    override suspend fun pay(
        subscriptionId: Ulid,
        method: SubscriptionPayMethod,
        idempotencyKey: String?,
    ): SubscriptionPayment = subscriptions.payModeBSubscription(
        subscriptionId = subscriptionId,
        // `periodMonth` is deliberately left absent: the contract defaults it to "the next due
        // period", which is server-side Asia/Colombo arithmetic (D-38). A client that computed the
        // month from the handset would pay the wrong one for five and a half hours a day.
        request = PayModeBSubscriptionRequest(method = method),
        idempotencyKey = idempotencyKey,
    )

    override suspend fun uploadSlip(paymentId: Ulid, slip: FileUpload, idempotencyKey: String?): SubscriptionPayment =
        subscriptions.uploadTransferSlip(paymentId = paymentId, file = slip, idempotencyKey = idempotencyKey)

    override suspend fun payments(subscriptionId: Ulid, page: PageRequest): Page<SubscriptionPayment> =
        subscriptions.listSubscriptionPayments(subscriptionId, page)

    override suspend fun ownerLankaQr(link: String): ByteArray? = SignedFileLink.parse(link)
        ?.takeIf { it.kind == ModeBFileKind.LANKAQR }
        ?.let { subscriptions.getModeBFile(it.kind, it.id, it.expires, it.signature) }
}
