package lk.mageride.passenger.subscription

import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.PayTo
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.data.models.subscription.SubscriptionCycle
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.data.models.subscription.SubscriptionStatus
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.util.BusinessCalendar

/**
 * The Mode B seam, in memory.
 *
 * A hand-written fake rather than `FakeApiBackend`: what cluster 6's view models decide is *which*
 * calls they make and in what order — the unsubscribe that must reach the live map, the pay that
 * must precede a slip upload, the statement read that fills a pill — and recording the calls is
 * the assertion. The HTTP layer between them is C013's and C019 already tests it.
 */
internal class FakeSubscriptionRepository : SubscriptionRepository {

    var subscriptions: List<Subscription> = emptyList()
    var accessRequestStatus: AccessRequestStatus = AccessRequestStatus.PENDING
    var payments: List<SubscriptionPayment> = emptyList()
    var payAnswer: SubscriptionPayment? = null
    var slipAnswer: SubscriptionPayment? = null
    var qrBytes: ByteArray? = null

    /** Thrown by the next call to whichever operation it names, then cleared. */
    var failWith: Throwable? = null

    val accessRequested = mutableListOf<Ulid>()
    val unsubscribed = mutableListOf<Ulid>()
    val paid = mutableListOf<Pair<Ulid, SubscriptionPayMethod>>()
    val slipsUploaded = mutableListOf<Ulid>()
    val qrLinksFetched = mutableListOf<String>()
    val idempotencyKeys = mutableListOf<String?>()

    override suspend fun requestAccess(vehicleId: Ulid, note: String?, idempotencyKey: String?): AccessRequest {
        raise()
        accessRequested += vehicleId
        idempotencyKeys += idempotencyKey
        return AccessRequest(
            requestId = REQUEST_ID,
            vehicleId = vehicleId,
            passengerId = Fixtures.PASSENGER_ID,
            status = accessRequestStatus,
            createdAt = Fixtures.NOW,
        )
    }

    override suspend fun subscriptions(passengerId: Ulid, page: PageRequest): Page<Subscription> {
        raise()
        return Page.of(subscriptions)
    }

    override suspend fun unsubscribe(subscriptionId: Ulid, idempotencyKey: String?): Subscription {
        raise()
        unsubscribed += subscriptionId
        val current = subscriptions.first { it.subscriptionId == subscriptionId }
        return current.copy(status = SubscriptionStatus.CANCELLED)
    }

    override suspend fun pay(
        subscriptionId: Ulid,
        method: SubscriptionPayMethod,
        idempotencyKey: String?,
    ): SubscriptionPayment {
        raise()
        paid += subscriptionId to method
        idempotencyKeys += idempotencyKey
        return payAnswer ?: payment(method, SubscriptionPaymentStatus.INITIATED)
    }

    override suspend fun uploadSlip(paymentId: Ulid, slip: FileUpload, idempotencyKey: String?): SubscriptionPayment {
        raise()
        slipsUploaded += paymentId
        return slipAnswer
            ?: payment(SubscriptionPayMethod.ONLINE_TRANSFER, SubscriptionPaymentStatus.PENDING_VERIFICATION)
    }

    override suspend fun payments(subscriptionId: Ulid, page: PageRequest): Page<SubscriptionPayment> {
        raise()
        return Page.of(payments)
    }

    override suspend fun ownerLankaQr(link: String): ByteArray? {
        qrLinksFetched += link
        return qrBytes
    }

    private fun raise() {
        failWith?.let {
            failWith = null
            throw it
        }
    }

    internal companion object {

        const val VEHICLE_ID: Ulid = "01JVEH00000000000000000001"
        const val SUBSCRIPTION_ID: Ulid = "01JSUB00000000000000000001"
        const val FREE_SUBSCRIPTION_ID: Ulid = "01JSUB00000000000000000002"
        const val REQUEST_ID: Ulid = "01JREQ00000000000000000001"

        /** The first of the Colombo month, which is the shape a `period_month` is CHECKed into. */
        val PERIOD: BusinessDate = BusinessCalendar.firstOfMonth(Fixtures.TODAY)

        /** Rs 6,000 a month — the wireframe's Office Van. */
        const val MONTHLY_FARE_MINOR: Long = 600_000L

        /** AL-49's verified block, as `POST …/pay` returns it. */
        val PAY_TO: PayTo = PayTo(
            lankaqrImageUrl = "https://api.mageride.lk/v1/mode-b/files/lankaqr/" +
                "01JPRF00000000000000000001?expires=1780000000&signature=abc123",
            bank = "Commercial Bank",
            branch = "Nugegoda",
            accountNo = "8001234567",
            accountHolderName = "ABC Fleet (Pvt) Ltd",
        )

        fun paidSubscription(subscriptionId: Ulid = SUBSCRIPTION_ID, vehicleId: Ulid = VEHICLE_ID): Subscription =
            Subscription(
                subscriptionId = subscriptionId,
                vehicleId = vehicleId,
                passengerId = Fixtures.PASSENGER_ID,
                billing = SubscriptionBilling.PAID,
                monthlyFareMinor = MONTHLY_FARE_MINOR,
                currency = Currency.LKR,
                cycle = SubscriptionCycle.MONTH_FIRST,
                nextDue = Fixtures.TODAY,
                status = SubscriptionStatus.ACTIVE,
            )

        /** Office transport: no fare at all (`ck_subscriptions_fare`). */
        fun freeSubscription(vehicleId: Ulid = "01JVEH00000000000000000002"): Subscription = Subscription(
            subscriptionId = FREE_SUBSCRIPTION_ID,
            vehicleId = vehicleId,
            passengerId = Fixtures.PASSENGER_ID,
            billing = SubscriptionBilling.FREE,
            cycle = SubscriptionCycle.MONTH_FIRST,
            status = SubscriptionStatus.ACTIVE,
        )

        fun payment(
            method: SubscriptionPayMethod,
            status: SubscriptionPaymentStatus,
            paymentId: Ulid = Fixtures.TRANSACTION_ID,
            month: BusinessDate = PERIOD,
        ): SubscriptionPayment = SubscriptionPayment(
            paymentId = paymentId,
            subscriptionId = SUBSCRIPTION_ID,
            method = method,
            amountMinor = MONTHLY_FARE_MINOR,
            currency = Currency.LKR,
            status = status,
            periodMonth = month,
            payTo = PAY_TO.takeIf { status == SubscriptionPaymentStatus.INITIATED },
            paidAt = Fixtures.NOW.takeIf { status == SubscriptionPaymentStatus.PAID },
        )
    }
}
