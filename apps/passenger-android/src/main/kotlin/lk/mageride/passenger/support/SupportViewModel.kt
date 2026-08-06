package lk.mageride.passenger.support

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/** Which of SCR-PA-030's two overlays is up. At most one, which is why it is an enum. */
internal enum class SupportSheet {

    /** **SCR-PA-030a** — issue description, related trip, attach screenshot, submit. */
    RAISE_TICKET,

    /** One ticket and its whole thread (US-16.2). */
    TICKET_THREAD,
}

/**
 * SCR-PA-030's state.
 *
 * @property search What is typed in *"🔍 Search help"*.
 * @property articles Every FAQ summary read, unfiltered; [visibleArticles] is what is drawn.
 * @property expandedArticleId The accordion row that is open, or `null` when all are closed.
 * @property expandedArticle That row's fetched body. `null` while the read is in flight.
 * @property tickets *"Your tickets"*.
 * @property trips The **Related trip** dropdown's options.
 * @property sheet Which overlay is up.
 * @property description What the passenger typed into the sheet.
 * @property tripId The selected **Related trip**.
 * @property screenshot The picked attachment, held until Submit uploads it.
 * @property ticket The open ticket and its thread.
 * @property loading The first read is in flight.
 * @property submitting Submit is in flight.
 * @property raisedTicketId The ticket the last successful submit opened.
 * @property error Resolved copy for the last failure.
 */
internal data class SupportState(
    val search: String = "",
    val articles: List<FaqSummary> = emptyList(),
    val expandedArticleId: Ulid? = null,
    val expandedArticle: FaqArticle? = null,
    val tickets: List<Ticket> = emptyList(),
    val trips: List<RideHistoryRow> = emptyList(),
    val sheet: SupportSheet? = null,
    val description: String = "",
    val tripId: Ulid? = null,
    val screenshot: PickedScreenshot? = null,
    val ticket: TicketDetail? = null,
    val loading: Boolean = true,
    val submitting: Boolean = false,
    val raisedTicketId: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {

    /**
     * The articles the search box leaves.
     *
     * Filtered **on the device**, over titles already read. `GET /v1/support/faq` takes a `category`
     * and no query string, so there is no server-side search to defer to; typing a word and getting
     * a request per keystroke would be worse than filtering a list that is a few dozen rows long.
     */
    val visibleArticles: List<FaqSummary>
        get() = if (search.isBlank()) {
            articles
        } else {
            articles.filter { it.title.contains(search.trim(), ignoreCase = true) }
        }

    /** Whether **Submit ticket** is live. A ticket with no description is nothing to act on. */
    val canSubmit: Boolean get() = !submitting && description.isNotBlank()
}

/**
 * **SCR-PA-030 · support, and SCR-PA-030a's raise-ticket sheet** (US-16.1, US-16.2).
 *
 * **The FAQ is an accordion, not a second screen.** D2' §SCR-PA-030 says *"FAQ accordion"* and the
 * wireframe draws a `＋` on each row, so opening one expands it in place — which is why the body is
 * held in state next to the id that is open rather than in a sheet. One at a time: a second `＋`
 * closes the first, because two open bodies push *"Your tickets"* off a 640 dp screen.
 *
 * **A ticket read is scoped to the passenger.** `GET /v1/support/tickets/{userId}` takes the user in
 * the path, so the id comes from the session rather than from anything a screen holds — and a
 * signed-out passenger simply has no ticket list, while the FAQ still works.
 *
 * **The screenshot is uploaded by Submit, not by the picker.** Two calls are the contract's shape
 * (the ticket takes an already-uploaded id), and doing the upload on Submit means a passenger who
 * opens the sheet, attaches a photo and changes their mind has cost the platform nothing. A failed
 * upload does **not** stop the ticket: what they wrote is the part support acts on.
 */
// Twelve members, and the count is the screen's: an FAQ half (read, search, expand a row), a ticket
// half (list, open a thread) and SCR-PA-030a's four-field draft with its submit. Splitting them
// would put the sheet's draft and the list it prepends to in two view models.
@Suppress("TooManyFunctions")
internal class SupportViewModel(
    private val support: SupportRepository,
    private val sessions: AuthSessionManager,
    private val preferences: AppPreferences,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SupportState())

    val state: StateFlow<SupportState> = mutableState.asStateFlow()

    private val userId: Ulid? get() = (sessions.state.value as? SessionState.SignedIn)?.userId

    init {
        refresh()
    }

    /**
     * Reads the FAQ and this passenger's tickets.
     *
     * The FAQ is fetched first and its result committed before the ticket read runs, which is what
     * makes a failure of the second leave the first on screen: *"search help"* still works for a
     * passenger whose ticket list could not be read.
     */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            // AL-26: the language the app is DRAWING in, not the profile's. See `SupportRepository`.
            val articles = runCatching { support.faq(preferences.language) }.getOrDefault(emptyList())
            mutableState.update { it.copy(articles = articles) }

            val tickets = userId?.let { support.tickets(it) }.orEmpty()
            mutableState.update { it.copy(loading = false, tickets = tickets) }
        }
    }

    /** The *"🔍 Search help"* field. Filters what has been read; sends nothing. */
    fun onSearchChange(raw: String) {
        mutableState.update { it.copy(search = raw) }
    }

    /**
     * The `＋` on an FAQ row (US-16.1).
     *
     * Tapping the open row closes it. Opening a different one replaces both the id and the body, so
     * the previous article's text can never be drawn under the new title while its read is in
     * flight.
     */
    fun toggleArticle(articleId: Ulid) {
        if (mutableState.value.expandedArticleId == articleId) {
            mutableState.update { it.copy(expandedArticleId = null, expandedArticle = null) }
            return
        }

        mutableState.update { it.copy(expandedArticleId = articleId, expandedArticle = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(expandedArticleId = null) } }) {
            val article = support.article(articleId, preferences.language)
            // Guarded: a slow read for a row the passenger has since closed must not re-open it.
            mutableState.update {
                if (it.expandedArticleId == articleId) it.copy(expandedArticle = article) else it
            }
        }
    }

    /** Opens SCR-PA-030a with an empty draft. */
    fun openTicketSheet() {
        mutableState.update {
            it.copy(
                sheet = SupportSheet.RAISE_TICKET,
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
        val user = userId ?: return
        mutableState.update { it.copy(sheet = SupportSheet.TICKET_THREAD, ticket = null, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(sheet = null) } }) {
            val detail = support.ticket(user, ticketId)
            mutableState.update { it.copy(ticket = detail) }
        }
    }

    /** Takes whichever overlay is up back down. */
    fun closeSheet() {
        mutableState.update { it.copy(sheet = null, ticket = null) }
    }

    /** SCR-PA-030a's **Issue description**. */
    fun onDescriptionChange(raw: String) {
        mutableState.update { it.copy(description = raw, error = null) }
    }

    /** SCR-PA-030a's **Related trip** dropdown. `null` clears the selection. */
    fun onTripSelected(tripId: Ulid?) {
        mutableState.update { it.copy(tripId = tripId) }
    }

    /** SCR-PA-030a's 📎. The bytes are held until Submit; nothing is sent yet. */
    fun onScreenshotPicked(image: PickedScreenshot?) {
        mutableState.update { it.copy(screenshot = image) }
    }

    /**
     * **Submit ticket** — the screenshot first, then the ticket that links it (US-16.2).
     *
     * A failed screenshot upload does **not** stop the ticket: what the passenger wrote is the part
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
                category = SupportCategories.GENERAL,
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
        viewModelScope.launch {
            val trips = runCatching { support.trips() }.getOrDefault(emptyList())
            mutableState.update { it.copy(trips = trips) }
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the passenger raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = SupportErrors.messageFor(cause)) }
            }
        }
    }
}
