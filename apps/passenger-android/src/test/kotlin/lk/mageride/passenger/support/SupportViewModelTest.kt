package lk.mageride.passenger.support

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.history.HistoryRepository
import lk.mageride.passenger.history.ScheduledRideRow
import lk.mageride.passenger.onboarding.FakeAppPreferences
import lk.mageride.passenger.subscription.signIn
import lk.mageride.passenger.subscription.signedInSession
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.support.SupportApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqListResponse
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketQueue
import lk.mageride.shared.data.models.support.TicketStatus
import lk.mageride.shared.data.models.support.UploadedScreenshot
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-030 / SCR-PA-030a, and the Definition-of-Done line *"FAQ content renders in the user's
 * selected language"*.
 *
 * That last one is the assertion this class exists for. The driver app leaves `?lang=` null and
 * lets support-svc fall back to the profile; **this app cannot**, because AL-26 makes the
 * passenger's language a device-first answer that the server write is allowed to lag
 * (`languagePendingSync`). So what has to be true is that the FAQ is asked for in the language the
 * app is *drawing in* — and that the same is true of an article opened from the accordion.
 */
class SupportViewModelTest {

    private val main = MainDispatcher()
    private val api = FakeSupportApi()
    private val history = FakeHistory()
    private val preferences = FakeAppPreferences(language = Language.SI)

    // Signed in through the real OTP flow — `GET /v1/support/tickets/{userId}` takes the user in
    // the path, and `SessionState.SignedIn` is minted in exactly one place.
    private val sessions = signedInSession()

    @BeforeTest
    fun setUp() {
        main.install()
        runBlocking { sessions.signIn() }
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_faq_is_asked_for_in_the_language_the_app_is_drawing_in() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(listOf<Language?>(Language.SI), api.faqLanguages, "AL-26 — the device's answer, not the profile's")
    }

    @Test
    fun an_article_opened_from_the_accordion_is_asked_for_in_the_same_language() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.toggleArticle(ARTICLE_ID)
        val state = model.state.await { it.expandedArticle != null }

        assertEquals(listOf<Language?>(Language.SI), api.articleLanguages)
        assertEquals(ARTICLE_ID, state.expandedArticleId)
    }

    @Test
    fun before_the_language_has_been_chosen_the_server_decides() = runBlocking {
        // A first run has no stored language, so `null` is sent — which is `SupportApi`'s documented
        // "use the caller's profile, then English". Sending a guess would be the client overriding
        // an answer it does not have.
        preferences.language = null
        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(listOf<Language?>(null), api.faqLanguages)
    }

    @Test
    fun one_faq_row_is_open_at_a_time_and_tapping_it_again_closes_it() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.toggleArticle(ARTICLE_ID)
        model.state.await { it.expandedArticle != null }

        model.toggleArticle(SECOND_ARTICLE_ID)
        val second = model.state.await { it.expandedArticleId == SECOND_ARTICLE_ID }
        assertNull(
            second.expandedArticle?.takeIf { it.articleId == ARTICLE_ID },
            "the previous body must never be drawn under the new title",
        )

        model.toggleArticle(SECOND_ARTICLE_ID)
        assertNull(model.state.await { it.expandedArticleId == null }.expandedArticle)
    }

    @Test
    fun a_ticket_list_that_fails_still_leaves_the_faq_on_screen() = runBlocking {
        // Help is what somebody who cannot reach their tickets came here for.
        api.ticketsFail = IllegalStateException("support-svc is down")
        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.articles.isNotEmpty())
        assertTrue(state.tickets.isEmpty())
    }

    @Test
    fun submitting_uploads_the_screenshot_first_and_links_what_it_answered() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet()
        model.onDescriptionChange("  The fare was wrong  ")
        model.onTripSelected(TRIP_ID)
        model.onScreenshotPicked(PickedScreenshot("s.jpg", byteArrayOf(1, 2, 3), "image/jpeg"))
        model.submit()

        val state = model.state.await { it.raisedTicketId != null }
        val raised = api.raised.single()

        assertEquals("The fare was wrong", raised.description, "trimmed, as the repository sends it")
        assertEquals(SupportCategories.GENERAL, raised.category, "US-14.13's queue, derived from this key")
        assertEquals(TRIP_ID, raised.tripId)
        assertEquals(UPLOAD_ID, raised.screenshotFileId)
        assertNull(state.sheet, "the sheet closes on success")
        assertEquals(listOf(state.raisedTicketId), state.tickets.map { it.ticketId }, "prepended, not re-read")
    }

    @Test
    fun a_failed_screenshot_upload_never_costs_the_passenger_their_ticket() = runBlocking {
        // What they wrote is the part support acts on. Losing a complaint because an image did not
        // go up would be the wrong trade.
        api.uploadFails = IllegalStateException("413")
        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet()
        model.onDescriptionChange("Driver took a long route")
        model.onScreenshotPicked(PickedScreenshot("s.jpg", byteArrayOf(9), "image/png"))
        model.submit()

        model.state.await { it.raisedTicketId != null }

        assertNull(api.raised.single().screenshotFileId, "the attachment is simply absent")
    }

    @Test
    fun an_empty_description_cannot_be_submitted() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet()
        model.onDescriptionChange("   ")
        assertFalse(model.state.value.canSubmit)

        model.submit()
        assertTrue(api.raised.isEmpty(), "a ticket with nothing in it is nothing to act on")
    }

    @Test
    fun the_related_trip_dropdown_is_the_past_rides_this_app_already_reads() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.openTicketSheet()
        val state = model.state.await { it.trips.isNotEmpty() }

        assertEquals(listOf(TRIP_ID), state.trips.map { it.rideId })
        assertEquals(1, history.reads, "one read per sheet opening, not one per keystroke")
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        SupportViewModel(
            support = SupportRepository(support = api, history = history),
            sessions = sessions,
            preferences = preferences,
        ),
    )

    private companion object {
        const val ARTICLE_ID = "01JFAQ00000000000000000001"
        const val SECOND_ARTICLE_ID = "01JFAQ00000000000000000002"
        const val TRIP_ID = "01JRIDE0000000000000000009"
        const val UPLOAD_ID = "01JUPL00000000000000000001"
        const val TICKET_ID = "01JTKT00000000000000000001"
    }

    /** support-svc in memory. The screen's whole question is which arguments went out. */
    private class FakeSupportApi : SupportApi {

        val faqLanguages = mutableListOf<Language?>()
        val articleLanguages = mutableListOf<Language?>()
        val raised = mutableListOf<CreateSupportTicketRequest>()

        var ticketsFail: Throwable? = null
        var uploadFails: Throwable? = null

        override suspend fun listFaqArticles(lang: Language?, category: String?): FaqListResponse {
            faqLanguages += lang
            return FaqListResponse(
                items = listOf(
                    summary(ARTICLE_ID, "How do I get a receipt?", lang),
                    summary(SECOND_ARTICLE_ID, "Payment failed — what now?", lang),
                ),
            )
        }

        override suspend fun getFaqArticle(articleId: Ulid, lang: Language?): FaqArticle {
            articleLanguages += lang
            return FaqArticle(
                articleId = articleId,
                title = "How do I get a receipt?",
                category = "billing",
                language = lang ?: Language.EN,
                body = "Open the trip and tap Receipt.",
            )
        }

        override suspend fun createSupportTicket(
            request: CreateSupportTicketRequest,
            idempotencyKey: String?,
        ): Ticket {
            raised += request
            return Ticket(
                ticketId = TICKET_ID,
                category = request.category,
                status = TicketStatus.OPEN,
                queue = TicketQueue.SUPPORT,
                tripId = request.tripId,
                createdAt = Fixtures.NOW,
            )
        }

        override suspend fun listSupportTickets(userId: Ulid, page: PageRequest): Page<Ticket> {
            ticketsFail?.let { throw it }
            return Page(items = emptyList())
        }

        override suspend fun getSupportTicket(userId: Ulid, ticketId: Ulid): TicketDetail = TicketDetail(
            ticketId = ticketId,
            category = SupportCategories.GENERAL,
            status = TicketStatus.OPEN,
            queue = TicketQueue.SUPPORT,
            createdAt = Fixtures.NOW,
            description = "The fare was wrong",
        )

        override suspend fun uploadSupportScreenshot(file: FileUpload, idempotencyKey: String?): UploadedScreenshot {
            uploadFails?.let { throw it }
            return UploadedScreenshot(fileId = UPLOAD_ID, sizeBytes = file.bytes.size.toLong())
        }

        override suspend fun getSupportScreenshot(uploadId: Ulid, expires: Long, signature: String): ByteArray =
            ByteArray(0)

        private fun summary(id: Ulid, title: String, lang: Language?) =
            FaqSummary(articleId = id, title = title, category = "billing", language = lang ?: Language.EN)
    }

    /** C081's history seam, answering the one row SCR-PA-030a's dropdown offers. */
    private class FakeHistory : HistoryRepository {

        var reads = 0

        override suspend fun rides(page: PageRequest): Page<RideHistoryRow> {
            reads++
            return Page(
                items = listOf(
                    RideHistoryRow(
                        rideId = TRIP_ID,
                        state = RideState.Paid,
                        pickup = Place(lat = 6.87, lng = 79.88, address = "Nugegoda"),
                        dropoff = Place(lat = 6.92, lng = 79.84, address = "Galle Face"),
                        completedAt = Fixtures.NOW,
                    ),
                ),
            )
        }

        override suspend fun trip(userId: Ulid, tripId: Ulid): TripDetail = error("not read by SCR-PA-030")

        override suspend fun ride(rideId: Ulid): RideDetail = error("not read by SCR-PA-030")

        override suspend fun scheduled(userId: Ulid): List<ScheduledRideRow> = emptyList()
    }
}
