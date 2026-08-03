package lk.mageride.driver.notifications

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.push.PushMessage
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * SCR-DA-034 — the inbox, and the one rule that is a security rule rather than a UI one.
 *
 * **A stored deep link is resolved, never trusted.** `deeplink` arrived over the network inside an
 * FCM payload and was written to disk verbatim; what the screen navigates to is whatever
 * `PushRouter` maps it onto, and an unrecognised value opens nothing at all.
 */
@OptIn(ExperimentalTime::class)
class NotificationsViewModelTest {

    private val main = MainDispatcher()
    private val inbox = FakeNotificationInbox()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_list_is_what_arrived_on_this_handset() = runBlocking {
        inbox.alerts += alert(id = "a", type = "LOW_BALANCE")
        inbox.alerts += alert(id = "b", type = "TOPUP_CONFIRMED")

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(listOf("a", "b"), state.alerts.map(DriverAlert::id))
    }

    @Test
    fun an_empty_inbox_is_distinguishable_from_one_that_has_not_loaded() = runBlocking {
        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.isEmpty, "the empty copy, not the shimmer")
    }

    @Test
    fun opening_an_alert_marks_it_read_and_follows_its_link() = runBlocking {
        inbox.alerts += alert(id = "a", type = "ride_offer", deeplink = "mageride://ride/${Fixtures.RIDE_ID}")

        val model = viewModel()
        val state = model.state.await { !it.loading }

        model.open(state.alerts.first())

        assertEquals(DriverRoute.ActiveRide(Fixtures.RIDE_ID), model.state.value.opening)
        assertTrue(model.state.value.alerts.first().read, "marked read locally and at once")
        assertEquals(listOf("a"), inbox.markedRead)
    }

    @Test
    fun a_hostile_or_unknown_link_opens_nothing_and_is_still_marked_read() = runBlocking {
        // The rule the shell states and this screen inherits. An alert that opens nothing is still
        // one the driver has looked at, which is all `read` claims.
        inbox.alerts += alert(id = "a", type = "BROADCAST", deeplink = "https://mageride.lk/admin")

        val model = viewModel()
        val state = model.state.await { !it.loading }
        model.open(state.alerts.first())

        assertNull(model.state.value.opening)
        assertTrue(model.state.value.alerts.first().read)
    }

    @Test
    fun an_alert_with_no_link_at_all_opens_nothing() = runBlocking {
        inbox.alerts += alert(id = "a", type = "REGISTRATION_RESULT", deeplink = null)

        val model = viewModel()
        val state = model.state.await { !it.loading }
        model.open(state.alerts.first())

        assertNull(model.state.value.opening)
    }

    @Test
    fun mark_all_read_clears_the_whole_list() = runBlocking {
        inbox.alerts += alert(id = "a", type = "LOW_BALANCE")
        inbox.alerts += alert(id = "b", type = "SOS_RESOLVED")

        val model = viewModel()
        model.state.await { !it.loading }
        model.markAllRead()

        assertTrue(model.state.value.alerts.all(DriverAlert::read))
        assertTrue(inbox.allMarked)
    }

    @Test
    fun a_storage_failure_stops_loading_rather_than_showing_an_error() = runBlocking {
        // There is no server call here and nothing a driver could do about a SQLite error, so the
        // screen falls back to its empty state — the same reasoning `ProofUploadQueue` applies.
        inbox.fails = true

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.isEmpty)
    }

    private fun alert(id: String, type: String, deeplink: String? = null) = DriverAlert(
        id = id,
        type = type,
        title = "Something happened",
        body = null,
        deeplink = deeplink,
        read = false,
        receivedAt = RECEIVED_AT,
    )

    private fun viewModel(): NotificationsViewModel = main.own(NotificationsViewModel(inbox = inbox))

    private companion object {
        val RECEIVED_AT: Instant = Fixtures.NOW
    }
}

/**
 * [NotificationInbox] in memory.
 *
 * The production one opens an encrypted SQLite file through an Android driver, whose local-unit-test
 * stub answers a default for every member — a screen tested against it would report an empty inbox
 * whatever had arrived, which is the one answer that makes this look like it works when it does not.
 */
@OptIn(ExperimentalTime::class)
private class FakeNotificationInbox : NotificationInbox {

    val alerts: MutableList<DriverAlert> = mutableListOf()
    val markedRead: MutableList<String> = mutableListOf()
    var allMarked: Boolean = false
    var fails: Boolean = false

    override suspend fun record(push: PushMessage, title: String?, body: String?) = Unit

    override suspend fun all(): List<DriverAlert> {
        if (fails) error("the database could not be opened")
        return alerts.toList()
    }

    override suspend fun markRead(id: String) {
        markedRead += id
    }

    override suspend fun markAllRead() {
        allMarked = true
    }
}
