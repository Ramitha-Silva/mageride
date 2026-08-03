package lk.mageride.driver.support

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail

/** Which of SCR-DA-033's three overlays is up. At most one, which is why it is an enum. */
internal enum class SupportSheet {

    /** **SCR-DA-033a** — issue description, related trip, attach screenshot, submit. */
    RAISE_TICKET,

    /** One ticket and its whole thread (US-16.2). */
    TICKET_THREAD,

    /** One FAQ article's body (US-16.1). */
    FAQ_ARTICLE,
}

/**
 * SCR-DA-033's state.
 *
 * @property search What is typed in *"🔍 Search help"*.
 * @property articles Every FAQ summary read, unfiltered; [visibleArticles] is what is drawn.
 * @property tickets *"Your tickets"*, newest first.
 * @property trips The **Related trip** dropdown's options.
 * @property sheet Which overlay is up.
 * @property draftCategory Which category [SupportSheet.RAISE_TICKET] will post — the quick action
 *   fixes it to [SupportCategories.DAILY_FEE_REFUND], the CTA leaves it general.
 * @property description What the driver typed into the sheet.
 * @property tripId The selected **Related trip**.
 * @property screenshot The picked attachment, held until Submit uploads it.
 * @property article The open FAQ article's body.
 * @property ticket The open ticket and its thread.
 * @property loading The first read is in flight.
 * @property submitting Submit is in flight.
 * @property raisedTicketId The ticket the last successful submit opened.
 * @property error Resolved copy for the last failure.
 */
internal data class SupportState(
    val search: String = "",
    val articles: List<FaqSummary> = emptyList(),
    val tickets: List<Ticket> = emptyList(),
    val trips: List<TripSummary> = emptyList(),
    val sheet: SupportSheet? = null,
    val draftCategory: String = SupportCategories.GENERAL,
    val description: String = "",
    val tripId: Ulid? = null,
    val screenshot: CapturedImage? = null,
    val article: FaqArticle? = null,
    val ticket: TicketDetail? = null,
    val loading: Boolean = true,
    val submitting: Boolean = false,
    val raisedTicketId: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {

    /**
     * The articles the search box leaves.
     *
     * Filtered **on the device**, over titles already read. `GET /v1/support/faq` takes a
     * `category` and no query string, so there is no server-side search to defer to; typing a word
     * and getting a request per keystroke would be worse than filtering a list that is a few dozen
     * rows long.
     */
    val visibleArticles: List<FaqSummary>
        get() = if (search.isBlank()) {
            articles
        } else {
            articles.filter { it.title.contains(search.trim(), ignoreCase = true) }
        }

    /** Whether **Submit ticket** is live. A ticket with no description is nothing to act on. */
    val canSubmit: Boolean get() = !submitting && description.isNotBlank()

    /** Whether the sheet is the daily-fee refund rather than a general ticket (US-9.23). */
    val isRefundRequest: Boolean get() = draftCategory == SupportCategories.DAILY_FEE_REFUND
}

/**
 * **SCR-DA-033 · support and the daily-fee refund** (US-16.1/16.2, US-9.23, US-14.11).
 *
 * **The refund request and *"Raise a ticket"* are ONE flow with two categories.** Both open
 * SCR-DA-033a and both post `POST /v1/support/tickets`; the quick action fixes
 * `category=daily_fee_refund`, which is what routes the row to Finance rather than Support. There
 * is no refund endpoint to call — see [SupportRepository]'s KDoc — and inventing a second screen
 * for a second value of one field would have been two ways to open the same ticket.
 *
 * **The screenshot is uploaded by Submit, not by the picker.** Two calls are the contract's shape
 * (the ticket takes an already-uploaded id), and doing the upload on Submit is the same choice C071
 * made for the delivery proof: a driver who opens the sheet, attaches a photo and changes their
 * mind has cost the platform nothing.
 *
 * **A ticket read is scoped to the driver.** `GET /v1/support/tickets/{userId}` takes the user in
 * the path, so the id comes from `DriverIdentity` rather than from anything the screen holds.
 */
// Twelve members, and the count is the screen's: an FAQ half (read, search, open an article), a
// ticket half (list, open a thread) and SCR-DA-033a's four-field draft with its submit. Splitting
// them would need the sheet's `category` to be agreed between two view models, which is the one
// field on this surface that decides which back-office queue a driver's complaint lands in.
@Suppress("TooManyFunctions")
internal class SupportViewModel(private val identity: DriverIdentity, private val support: SupportRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(SupportState())

    val state: StateFlow<SupportState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /**
     * Reads the FAQ and this driver's tickets.
     *
     * The FAQ is public-ish and the tickets are not, so a failure to resolve the driver leaves the
     * articles up rather than emptying the screen: *"search help"* still works for a driver whose
     * ticket list could not be read.
     */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            // Folded in BEFORE the ticket read rather than after both, which is what makes the
            // sentence above true: a ticket list that could not be read must leave the articles up.
            val articles = runCatching { support.faq() }.getOrDefault(emptyList())
            mutableState.update { it.copy(articles = articles) }

            val tickets = identity.driverId?.let { support.tickets(it) }.orEmpty()
            mutableState.update { it.copy(loading = false, tickets = tickets) }
        }
    }

    /** The *"🔍 Search help"* field. Filters what has been read; sends nothing. */
    fun onSearchChange(raw: String) {
        mutableState.update { it.copy(search = raw) }
    }

    /**
     * Opens SCR-DA-033a.
     *
     * @param category [SupportCategories.DAILY_FEE_REFUND] from the quick action's *"Request"*,
     *   [SupportCategories.GENERAL] from the *"Raise a ticket"* CTA.
     */
    fun openTicketSheet(category: String) {
        mutableState.update {
            it.copy(
                sheet = SupportSheet.RAISE_TICKET,
                draftCategory = category,
                description = "",
                tripId = null,
                screenshot = null,
                raisedTicketId = null,
                error = null,
            )
        }
        readTrips()
    }

    /** Opens one ticket's thread. */
    fun openTicket(ticketId: Ulid) {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(sheet = SupportSheet.TICKET_THREAD, ticket = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(sheet = null) } }) {
            val detail = support.ticket(driverId, ticketId)
            mutableState.update { it.copy(ticket = detail) }
        }
    }

    /** Opens one FAQ article's body (US-16.1). */
    fun openArticle(articleId: Ulid) {
        mutableState.update { it.copy(sheet = SupportSheet.FAQ_ARTICLE, article = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(sheet = null) } }) {
            val article = support.article(articleId)
            mutableState.update { it.copy(article = article) }
        }
    }

    /** Takes whichever overlay is up back down. */
    fun closeSheet() {
        mutableState.update { it.copy(sheet = null, article = null, ticket = null) }
    }

    /** SCR-DA-033a's **Issue description**. */
    fun onDescriptionChange(raw: String) {
        mutableState.update { it.copy(description = raw, error = null) }
    }

    /** SCR-DA-033a's **Related trip** dropdown. `null` clears the selection. */
    fun onTripSelected(tripId: Ulid?) {
        mutableState.update { it.copy(tripId = tripId) }
    }

    /** SCR-DA-033a's 📎. The bytes are held until Submit; nothing is sent yet. */
    fun onScreenshotPicked(image: CapturedImage?) {
        mutableState.update { it.copy(screenshot = image) }
    }

    /**
     * **Submit ticket** — the screenshot first, then the ticket that links it (US-16.2).
     *
     * A failed screenshot upload does **not** stop the ticket: what the driver wrote is the part
     * support acts on, and losing a complaint because an image did not go up would be the wrong
     * trade. The attachment is simply absent, and the ticket says everything else.
     */
    fun submit() {
        val current = mutableState.value
        if (!current.canSubmit) return

        mutableState.update { it.copy(submitting = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(submitting = false) } }) {
            val fileId = current.screenshot?.let { image ->
                runCatching { support.uploadScreenshot(image) }.getOrNull()
            }

            val raised = support.raise(
                category = current.draftCategory,
                description = current.description,
                tripId = current.tripId,
                screenshotFileId = fileId,
            )

            // Prepended rather than re-read: `POST` answers with the row, and a list re-read would
            // be a round trip to learn what the response already said.
            mutableState.update {
                it.copy(
                    submitting = false,
                    sheet = null,
                    description = "",
                    tripId = null,
                    screenshot = null,
                    raisedTicketId = raised.ticketId,
                    tickets = listOf(raised) + it.tickets,
                )
            }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /** The **Related trip** options, read once per sheet opening. */
    private fun readTrips() {
        if (mutableState.value.trips.isNotEmpty()) return
        val driverId = identity.driverId ?: return
        viewModelScope.launch {
            val trips = runCatching { support.trips(driverId) }.getOrDefault(emptyList())
            mutableState.update { it.copy(trips = trips) }
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }
}
