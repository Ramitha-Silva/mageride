package lk.mageride.shared.testing.scenario

import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.SubscriberRow
import lk.mageride.shared.data.models.subscription.SubscriberStatus
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.data.models.subscription.SubscriptionCycle
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.data.models.subscription.SubscriptionStatus
import lk.mageride.shared.domain.subscription.ModeBBilling
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fixture.Fixtures

/**
 * A passenger subscribes to a Mode B vehicle and pays two monthly cycles.
 *
 * The fourth canonical scenario, and the one that is not a ride: Mode B is a **monthly
 * subscription to a fleet owner's vehicle**, with no per-trip fare and no ride aggregate anywhere
 * in it (D5' §1.1). Three things about it are easy to get wrong and are therefore what this
 * scenario is shaped to make visible:
 *
 * 1. **The money is a pass-through** (AL-24, §18b). MageRide holds none of it and takes no
 *    commission, so nothing here produces a wallet `LedgerEntry` — and `MoneyDomainHygieneTest`
 *    fails the build if that ever changes.
 * 2. **The cycle is Asia/Colombo calendar arithmetic** (BR-23.9, D-38). [joinDate] is a business
 *    date and [dueDates] comes from [ModeBBilling], not from adding thirty days.
 * 3. **Cash has no gateway leg.** The second month is paid `cash`, which the owner marks received
 *    — it goes `initiated → paid` with nothing in between, where an `online_transfer` would sit in
 *    `pending_verification` until the owner confirms the slip.
 */
public object ModeBSubscription {

    /** Rs 6,500.00 a month — a school-run seat, the archetypal Mode B subscription. */
    public val monthlyFare: Money = Money(amountMinor = 650_000L, currency = Currency.LKR)

    /** When the passenger joined, in Colombo. */
    public val joinDate: BusinessDate = Fixtures.TODAY

    /** The cycle: the roster rolls on the 1st. */
    public val cycle: SubscriptionCycle = SubscriptionCycle.MONTH_FIRST

    /** The first two due dates, derived through [ModeBBilling] rather than typed out. */
    public val dueDates: List<BusinessDate> = ModeBBilling.firstDueDate(cycle, joinDate)
        .let { first -> listOf(first, ModeBBilling.rollForward(first)) }

    /** The subscription as `GET /v1/mode-b/subscriptions` reads it, once active. */
    public val subscription: Subscription = Subscription(
        subscriptionId = Fixtures.SUBSCRIPTION_ID,
        vehicleId = Fixtures.VEHICLE_ID,
        passengerId = Fixtures.PASSENGER_ID,
        billing = SubscriptionBilling.PAID,
        monthlyFareMinor = monthlyFare.amountMinor,
        currency = Currency.LKR,
        cycle = cycle,
        nextDue = dueDates.first(),
        nextDueTzAt = Fixtures.NOW,
        status = SubscriptionStatus.ACTIVE,
    )

    /** The same subscriber, as the fleet owner's roster shows them before the first payment. */
    public val rosterRow: SubscriberRow = SubscriberRow(
        subscriberId = Fixtures.SUBSCRIBER_ID,
        passengerId = Fixtures.PASSENGER_ID,
        name = "A. Perera",
        mobileMasked = Fixtures.PASSENGER_PHONE_MASKED,
        billing = SubscriptionBilling.PAID,
        monthlyFareMinor = monthlyFare.amountMinor,
        currency = Currency.LKR,
        cycle = cycle,
        thisMonthStatus = SubscriberMonthStatus.UNPAID,
        muted = false,
        status = SubscriberStatus.ACTIVE,
    )

    /** Month one, paid through OnePay: `initiated` then `paid` once the gateway calls back. */
    public val firstMonth: List<SubscriptionPayment> = listOf(
        payment(dueDates[0], SubscriptionPayMethod.ONEPAY, SubscriptionPaymentStatus.INITIATED),
        payment(dueDates[0], SubscriptionPayMethod.ONEPAY, SubscriptionPaymentStatus.PAID),
    )

    /** Month two, paid in cash: no gateway leg at all — the owner marks it received. */
    public val secondMonth: List<SubscriptionPayment> = listOf(
        payment(dueDates[1], SubscriptionPayMethod.CASH, SubscriptionPaymentStatus.INITIATED),
        payment(dueDates[1], SubscriptionPayMethod.CASH, SubscriptionPaymentStatus.PAID),
    )

    /** Every payment state either month passes through, in order. */
    public val payments: List<SubscriptionPayment> = firstMonth + secondMonth

    /**
     * Programs [backend] so the subscription clients reproduce the two cycles.
     *
     * `listSubscriptionPayments` walks the four payment states one call at a time, which is what
     * lets a poll-until-settled test run against the fake without stubbing each call by hand.
     */
    public fun install(backend: FakeApiBackend): FakeApiBackend {
        backend.returns("listPassengerSubscriptions", Page.of(listOf(subscription)))
        backend.returns("listModeBSubscribers", Page.of(listOf(rosterRow)))
        backend.returns("payModeBSubscription", firstMonth.first())
        backend.next(
            "listSubscriptionPayments",
            *payments.map { FakeReply.value(Page.of(listOf(it))) }.toTypedArray(),
        )
        return backend
    }

    private fun payment(
        month: BusinessDate,
        method: SubscriptionPayMethod,
        status: SubscriptionPaymentStatus,
    ): SubscriptionPayment = SubscriptionPayment(
        paymentId = Fixtures.TRANSACTION_ID,
        subscriptionId = Fixtures.SUBSCRIPTION_ID,
        method = method,
        amountMinor = monthlyFare.amountMinor,
        currency = Currency.LKR,
        status = status,
        // A `period_month` is the first of the month, in Colombo, and is CHECKed into that shape
        // server-side (C005). Deriving it keeps the fixture honest when TODAY moves.
        periodMonth = month,
        periodMonthTzAt = Fixtures.NOW,
        paidAt = Fixtures.NOW.takeIf { status == SubscriptionPaymentStatus.PAID },
    )
}
