package lk.mageride.passenger.subscription

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.util.BusinessCalendar
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-025b — the subscriber's statement (US-23.9).
 *
 * The wireframe prints June above May above April, and `GET …/payments` promises no order at all —
 * so the ordering is this screen's and is asserted rather than assumed. A statement that listed
 * April at the top reads as a payment nobody made.
 */
class SubscriptionPaymentsViewModelTest {

    private val main = MainDispatcher()
    private val repository = FakeSubscriptionRepository()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_statement_is_newest_month_first_whatever_order_the_server_used() = runBlocking {
        val june = FakeSubscriptionRepository.PERIOD
        val may = BusinessCalendar.plusMonths(june, -1)
        val april = BusinessCalendar.plusMonths(june, -2)

        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
        repository.payments = listOf(
            row(april, SubscriptionPayMethod.CASH, SubscriptionPaymentStatus.PAID, "01JPAY00000000000000000001"),
            row(june, SubscriptionPayMethod.LANKAQR_SCAN, SubscriptionPaymentStatus.PAID, "01JPAY00000000000000000003"),
            row(
                may,
                SubscriptionPayMethod.ONLINE_TRANSFER,
                SubscriptionPaymentStatus.PENDING_VERIFICATION,
                "01JPAY00000000000000000002",
            ),
        )

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(listOf(june, may, april), state.payments.map { it.periodMonth })
        assertFalse(state.empty)
    }

    @Test
    fun the_three_statuses_the_wireframe_prints_all_survive_the_round_trip() = runBlocking {
        // Paid / Pending verification / Paid · cash — and the header's standing monthly fare,
        // which the statement itself does not carry.
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
        repository.payments = listOf(
            row(
                FakeSubscriptionRepository.PERIOD,
                SubscriptionPayMethod.ONLINE_TRANSFER,
                SubscriptionPaymentStatus.PENDING_VERIFICATION,
                "01JPAY00000000000000000002",
            ),
        )

        val model = viewModel()
        val state = model.state.await { it.subscription != null }

        assertEquals(SubscriptionPaymentStatus.PENDING_VERIFICATION, state.payments.single().status)
        assertEquals(FakeSubscriptionRepository.MONTHLY_FARE_MINOR, state.fare?.amountMinor)
    }

    @Test
    fun a_subscription_with_no_payments_yet_is_empty_rather_than_still_loading() = runBlocking {
        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.empty)
    }

    // ------------------------------------------------------------------------------------------

    private fun row(
        month: lk.mageride.shared.data.models.BusinessDate,
        method: SubscriptionPayMethod,
        status: SubscriptionPaymentStatus,
        paymentId: String,
    ) = FakeSubscriptionRepository.payment(method = method, status = status, paymentId = paymentId, month = month)

    private fun viewModel() = main.own(
        SubscriptionPaymentsViewModel(
            subscriptionId = FakeSubscriptionRepository.SUBSCRIPTION_ID,
            subscriptions = repository,
            sessions = session(),
        ),
    )

    private fun session(): AuthSessionManager = signedInSession().also { runBlocking { it.signIn() } }
}
