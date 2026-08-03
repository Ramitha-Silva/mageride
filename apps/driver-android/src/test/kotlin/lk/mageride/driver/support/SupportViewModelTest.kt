package lk.mageride.driver.support

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.support.FaqListResponse
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketQueue
import lk.mageride.shared.data.models.support.TicketStatus
import lk.mageride.shared.data.models.support.UploadedScreenshot
import lk.mageride.shared.testing.fake.FakeApiBackend
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
 * SCR-DA-033 / SCR-DA-033a — the FAQ, the ticket queue and the daily-fee refund.
 *
 * The one thing on this surface that is easy to get wrong and expensive to get wrong: **the refund
 * request is a `category`, not an endpoint**, and that category is what routes the row to Finance
 * rather than to Support (US-9.23 / US-14.11). A ticket raised with the wrong key lands in a queue
 * that cannot reverse a fee.
 */
class SupportViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listFaqArticles", FaqListResponse(items = listOf(article())))
        backend.returns("listSupportTickets", Page(items = listOf(ticket())))
        backend.returns("listTrips", Page(items = emptyList<TripSummary>()))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_refund_quick_action_raises_a_finance_category_ticket() = runBlocking {
        backend.returns("createSupportTicket", ticket(category = SupportCategories.DAILY_FEE_REFUND))

        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet(SupportCategories.DAILY_FEE_REFUND)
        assertTrue(model.state.value.isRefundRequest)

        model.onDescriptionChange("The app crashed when I went online.")
        model.submit()
        model.state.await { it.raisedTicketId != null }

        val body = backend.lastCall("createSupportTicket").json
        assertEquals(
            SupportCategories.DAILY_FEE_REFUND,
            body["category"]?.toString()?.trim('"'),
            "US-9.23's key is what derives TicketQueue.FINANCE",
        )
    }

    @Test
    fun the_raise_ticket_cta_raises_an_ordinary_support_ticket() = runBlocking {
        backend.returns("createSupportTicket", ticket())

        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet(SupportCategories.GENERAL)
        assertFalse(model.state.value.isRefundRequest)

        model.onDescriptionChange("A passenger left a bag in the tuk.")
        model.submit()
        model.state.await { it.raisedTicketId != null }

        assertEquals(
            SupportCategories.GENERAL,
            backend.lastCall("createSupportTicket").json["category"]?.toString()?.trim('"'),
        )
    }

    @Test
    fun a_new_ticket_appears_in_the_list_without_a_re_read() = runBlocking {
        // The POST answers with the row, so re-reading the list would be a round trip spent
        // learning what the response already said.
        backend.returns("createSupportTicket", ticket(ticketId = SECOND_TICKET_ID))

        val model = viewModel()
        model.state.await { !it.loading }
        assertEquals(1, model.state.value.tickets.size)

        model.openTicketSheet(SupportCategories.GENERAL)
        model.onDescriptionChange("Something went wrong.")
        model.submit()

        val state = model.state.await { it.raisedTicketId != null }
        assertEquals(SECOND_TICKET_ID, state.tickets.first().ticketId, "newest first")
        assertEquals(2, state.tickets.size)
        assertNull(state.sheet, "the sheet closes on a successful submit")
        assertEquals(1, backend.callsTo("listSupportTickets").size, "no second list read")
    }

    @Test
    fun the_screenshot_is_uploaded_first_and_its_id_is_what_the_ticket_carries() = runBlocking {
        backend.returns("uploadSupportScreenshot", UploadedScreenshot(fileId = UPLOAD_ID, sizeBytes = 4))
        backend.returns("createSupportTicket", ticket())

        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet(SupportCategories.GENERAL)
        model.onDescriptionChange("See the attached screen.")
        model.onScreenshotPicked(screenshot())
        model.submit()
        model.state.await { it.raisedTicketId != null }

        assertTrue(backend.called("uploadSupportScreenshot"))
        assertEquals(
            UPLOAD_ID,
            backend.lastCall("createSupportTicket").json["screenshotFileId"]?.toString()?.trim('"'),
        )
    }

    @Test
    fun a_failed_screenshot_upload_does_not_lose_the_ticket() = runBlocking {
        // What the driver wrote is the part support acts on. Losing a complaint because an image
        // did not go up would be the wrong trade.
        backend.fails("uploadSupportScreenshot", HttpStatusCode.PayloadTooLarge, "payload-too-large")
        backend.returns("createSupportTicket", ticket())

        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet(SupportCategories.GENERAL)
        model.onDescriptionChange("The meter froze mid-trip.")
        model.onScreenshotPicked(screenshot())
        model.submit()
        model.state.await { it.raisedTicketId != null }

        assertTrue(backend.called("createSupportTicket"))
        assertNull(backend.lastCall("createSupportTicket").json["screenshotFileId"], "no id to link")
    }

    @Test
    fun submit_is_refused_until_something_is_written() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet(SupportCategories.GENERAL)
        assertFalse(model.state.value.canSubmit)

        model.submit()
        assertFalse(backend.called("createSupportTicket"))
    }

    @Test
    fun the_search_filters_the_articles_already_read_and_sends_nothing() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.onSearchChange("daily")
        assertEquals(1, model.state.value.visibleArticles.size)

        model.onSearchChange("insurance")
        assertTrue(model.state.value.visibleArticles.isEmpty())

        // One read on open, and none per keystroke: `GET /v1/support/faq` takes a category and no
        // query string, so there is no server-side search to defer to.
        assertEquals(1, backend.callsTo("listFaqArticles").size)
    }

    @Test
    fun a_ticket_list_that_cannot_be_read_still_leaves_the_faq_up() = runBlocking {
        backend.fails("listSupportTickets", HttpStatusCode.ServiceUnavailable, "service-unavailable")

        val model = viewModel()
        val state = model.state.await { !it.loading || it.error != null }

        assertTrue(state.articles.isNotEmpty(), "search help still works")
    }

    private fun article() = FaqSummary(
        articleId = ARTICLE_ID,
        title = "Daily fee: when is it charged?",
        category = "wallet",
        language = Language.EN,
    )

    private fun ticket(ticketId: Ulid = TICKET_ID, category: String = SupportCategories.GENERAL) = Ticket(
        ticketId = ticketId,
        category = category,
        status = TicketStatus.OPEN,
        queue = if (category == SupportCategories.DAILY_FEE_REFUND) TicketQueue.FINANCE else TicketQueue.SUPPORT,
        createdAt = Fixtures.NOW,
    )

    private fun screenshot() = CapturedImage(fileName = "shot.jpg", bytes = byteArrayOf(1, 2, 3, 4))

    private suspend fun viewModel(): SupportViewModel {
        val api = backend.mageRideApi()
        return main.own(
            SupportViewModel(
                identity = identity(backend, signedInSessions(backend)),
                support = SupportRepository(support = api.support, query = api.query),
            ),
        )
    }

    private companion object {
        const val ARTICLE_ID: Ulid = "01JFAQ0000000000000000001"
        const val TICKET_ID: Ulid = "01JTICKET000000000000001"
        const val SECOND_TICKET_ID: Ulid = "01JTICKET000000000000002"
        const val UPLOAD_ID: Ulid = "01JUPLOAD000000000000001"
    }
}
