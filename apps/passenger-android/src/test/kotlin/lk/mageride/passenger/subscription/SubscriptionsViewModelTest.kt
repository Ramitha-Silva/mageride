package lk.mageride.passenger.subscription

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.live.FakeLiveHubTransport
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.platform.platformH3Grid
import lk.mageride.shared.realtime.LiveHub
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-025, and the Definition-of-Done line *"unsubscribing removes the vehicle from the live
 * map within seconds"*.
 *
 * That line is asserted against a **real** [PassengerLiveMap] over the fake socket rather than
 * against a mock, because the interesting part is the join between two subsystems: the unsubscribe
 * is subscription-svc's and the marker is fanout-svc's, and the client is what closes the gap
 * before `share.revoked` comes back (D-22, AL-25).
 */
class SubscriptionsViewModelTest {

    private val main = MainDispatcher()
    private val repository = FakeSubscriptionRepository()
    private val transport = FakeLiveHubTransport()
    private val scopes = mutableListOf<CoroutineScope>()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        scopes.forEach(CoroutineScope::cancel)
        main.uninstall()
    }

    @Test
    fun a_paid_card_carries_a_fare_and_a_free_one_carries_none() = runBlocking {
        // BR-23.8 — Free is office and staff transport: no fee, and no payment UI at all. The
        // absence of a fare is `ck_subscriptions_fare`'s shape, not a missing field.
        repository.subscriptions = listOf(
            FakeSubscriptionRepository.paidSubscription(),
            FakeSubscriptionRepository.freeSubscription(),
        )

        val model = viewModel()
        val state = model.state.await { !it.loading }

        val paid = state.cards.first { it.subscription.subscriptionId == FakeSubscriptionRepository.SUBSCRIPTION_ID }
        val free = state.cards.first {
            it.subscription.subscriptionId == FakeSubscriptionRepository.FREE_SUBSCRIPTION_ID
        }

        assertEquals(FakeSubscriptionRepository.MONTHLY_FARE_MINOR, paid.fare?.amountMinor)
        assertTrue(paid.paid, "💳 Pay and 🧾 are drawn")
        assertNull(free.fare)
        assertFalse(free.paid, "a Free vehicle has nothing to pay and no statement to read")
    }

    @Test
    fun a_transfer_the_owner_has_not_confirmed_shows_pending_verification_on_the_card() = runBlocking {
        // The Definition-of-Done line "an online-transfer payment shows Pending verification until
        // the owner confirms", seen from SCR-PA-025 rather than from the statement. A passenger
        // who has already sent the money must not be told they owe it.
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
        repository.payments = listOf(
            FakeSubscriptionRepository.payment(
                method = SubscriptionPayMethod.ONLINE_TRANSFER,
                status = SubscriptionPaymentStatus.PENDING_VERIFICATION,
            ),
        )

        val model = viewModel()
        val state = model.state.await { it.cards.firstOrNull()?.monthStatus != null }

        assertEquals(SubscriberMonthStatus.PENDING_VERIFICATION, state.cards.single().monthStatus)
    }

    @Test
    fun the_pill_is_read_from_the_latest_month_and_not_from_the_first_row_returned() = runBlocking {
        // `GET …/payments` fixes no ordering, so the newest period is taken by comparison. A
        // client that trusted position would print April's status over June's.
        val june = FakeSubscriptionRepository.PERIOD
        val may = lk.mageride.shared.util.BusinessCalendar.plusMonths(june, -1)
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
        repository.payments = listOf(
            FakeSubscriptionRepository.payment(
                method = SubscriptionPayMethod.LANKAQR_SCAN,
                status = SubscriptionPaymentStatus.PAID,
                month = june,
                paymentId = "01JPAY00000000000000000002",
            ),
            FakeSubscriptionRepository.payment(
                method = SubscriptionPayMethod.CASH,
                status = SubscriptionPaymentStatus.INITIATED,
                month = may,
                paymentId = "01JPAY00000000000000000001",
            ),
        ).reversed()

        val model = viewModel()
        val state = model.state.await { it.cards.firstOrNull()?.monthStatus != null }

        assertEquals(SubscriberMonthStatus.PAID, state.cards.single().monthStatus)
    }

    @Test
    fun unsubscribing_drops_the_card_and_the_marker_without_waiting_for_the_socket() = runBlocking {
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())

        val live = liveMap()
        live.connect()
        transport.emit(
            LiveHub.Event.VEHICLE_POSITIONS,
            """[{"vehicleId":"${FakeSubscriptionRepository.VEHICLE_ID}",""" +
                """"lat":6.9271,"lng":79.8612,"type":"van","mode":"B"}]""",
        )
        assertEquals(1, live.vehicles.value.size, "the subscribed van is on the map")

        val model = viewModel(live)
        val state = model.state.await { !it.loading }
        val card = state.cards.single()

        // The ✕ asks first: AL-25 makes this irreversible in place.
        model.confirmUnsubscribe(card)
        assertEquals(card, model.state.value.confirming)
        assertTrue(repository.unsubscribed.isEmpty(), "the dialog alone changes nothing")

        model.unsubscribe(card)
        model.state.await { it.cards.isEmpty() }

        assertEquals(listOf(FakeSubscriptionRepository.SUBSCRIPTION_ID), repository.unsubscribed)
        assertTrue(live.vehicles.value.isEmpty(), "the marker went with the grant — no ShareRevoked needed")

        // And nothing was sent to the hub: `signalr-hub.md` §2 has four client → server methods and
        // none of them leaves a vehicle group. Membership is the server's (D-23).
        assertTrue(transport.calls.isEmpty(), "the client never asks to leave a vehicle group")
    }

    @Test
    fun a_failed_unsubscribe_leaves_the_card_where_it_was() = runBlocking {
        // The row is removed on the response, not on the tap: a passenger whose unsubscribe failed
        // still has the subscription, and hiding it would mean they could not try again.
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())
        val model = viewModel()
        val card = model.state.await { !it.loading }.cards.single()

        repository.failWith = IllegalStateException("the network went away")
        model.unsubscribe(card)
        val state = model.state.await { it.error != null }

        assertEquals(1, state.cards.size)
        assertNull(state.leaving)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(live: PassengerLiveMap = liveMap()) = main.own(
        SubscriptionsViewModel(
            subscriptions = repository,
            sessions = session(),
            live = live,
            keys = { KEY },
        ),
    )

    private fun session(): AuthSessionManager = signedInSession().also { runBlocking { it.signIn() } }

    /**
     * A live map on [Dispatchers.Unconfined].
     *
     * Unconfined rather than a `TestDispatcher` because these are `runBlocking` tests over
     * `MainDispatcher` — see its KDoc. It also makes `dropVehicle`'s `scope.launch` run inline,
     * which is what lets the assertion be "the marker is gone" rather than "the marker will be
     * gone once something advances a scheduler".
     */
    private fun liveMap(): PassengerLiveMap {
        val backend = FakeApiBackend().always(
            "getNearbyVehicles",
            FakeReply.value(NearbyVehiclesResponse(vehicles = emptyList(), asOf = Fixtures.NOW)),
        )
        val scope = CoroutineScope(Dispatchers.Unconfined + Job()).also(scopes::add)
        return PassengerLiveMap(
            transport = transport,
            query = backend.mageRideApi().query,
            grid = requireNotNull(platformH3Grid()) { "com.uber:h3 should be on the unit-test classpath" },
            scope = scope,
        )
    }

    private companion object {
        const val KEY = "01JIDEMPOTENCY000000000002"
    }
}
