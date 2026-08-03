package lk.mageride.driver.support

import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.support.SupportApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail

/**
 * SCR-DA-033's data — support-svc's FAQ and ticket queue, plus the trips the ticket sheet attaches
 * to (US-16.2, US-9.23).
 *
 * ### The daily-fee refund is a CATEGORY, not an endpoint
 *
 * The wireframe's *"Request daily-fee refund"* quick action and its *"Raise a ticket"* sheet post to
 * the **same** `POST /v1/support/tickets`; what differs is `category`. `SupportModels` is explicit
 * about why — *"`support.tickets.category` carries no CHECK, and the driver daily-fee refund request
 * (US-9.23) is deliberately modelled as an ordinary category rather than its own endpoint"* — and
 * support-svc derives the queue from it, so `daily_fee_refund` routes to **Finance** (US-14.11) and
 * everything else to Support. A second route would have been a second way to open the same row.
 *
 * ### The trip dropdown is the ride-history read model, not a second one
 *
 * *"Related trip · DRV-22011-0617 · Galle Face → Nugegoda"* is `GET /v1/trips/{driverId}` — the
 * same call SCR-DA-030 draws its list from (C074), and the only read that spans both planes for a
 * driver. It is reached directly rather than through `RideHistoryRepository` because that class's
 * other three methods are the rating door and this screen must not grow one.
 *
 * ### `?lang=` is deliberately left null
 *
 * `SupportApi`'s own KDoc: the server falls back to the caller's profile language and then to
 * English, *"and that is one fewer place for the app's idea of the current language to disagree
 * with the profile's"*. [faq] takes a [Language] for the caller that has a reason to override it;
 * this screen does not pass one.
 */
internal class SupportRepository(private val support: SupportApi, private val query: QueryApi) {

    /** `GET /v1/support/faq` — the article summaries behind *"🔍 Search help"* (US-16.1). */
    suspend fun faq(language: Language? = null): List<FaqSummary> = support.listFaqArticles(lang = language).items

    /** `GET /v1/support/faq/{articleId}` — one article's markdown body. */
    suspend fun article(articleId: Ulid, language: Language? = null) = support.getFaqArticle(articleId, language)

    /** `GET /v1/support/tickets/{userId}` — *"Your tickets"*, newest first. */
    suspend fun tickets(userId: Ulid): List<Ticket> = support.listSupportTickets(userId, PageRequest.FIRST).items

    /**
     * `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket **with its thread**.
     *
     * The thread is what makes a resolution readable rather than a status the driver has to
     * interpret: it carries every transition and every agent reply, oldest first. The agent's
     * identity is never in it — `TicketEvent.actorRole` is a role, by contract.
     */
    suspend fun ticket(userId: Ulid, ticketId: Ulid): TicketDetail = support.getSupportTicket(userId, ticketId)

    /** `GET /v1/trips/{driverId}` — the **Related trip** dropdown's options. */
    suspend fun trips(driverId: Ulid): List<TripSummary> = query.listTrips(driverId, PageRequest.FIRST).items

    /**
     * `POST /v1/support/screenshots` — the sheet's 📎 attachment (Δ C053).
     *
     * Two calls rather than one multipart ticket, because that is the contract's shape:
     * `POST /v1/support/tickets` takes an **already-uploaded** `docs.uploads` id. A file is
     * attachable to at most one ticket and only by the user who uploaded it, so an upload whose
     * ticket is never submitted is an orphan the 90-day sweep collects — which is the right
     * trade against holding several megabytes in a view model until Submit.
     */
    suspend fun uploadScreenshot(image: CapturedImage): Ulid = support.uploadSupportScreenshot(
        FileUpload(fileName = image.fileName, bytes = image.bytes, contentType = image.mimeType),
    ).fileId

    /**
     * `POST /v1/support/tickets` — SCR-DA-033a's Submit, and the refund quick action (US-16.2).
     *
     * @param category [SupportCategories.DAILY_FEE_REFUND] routes to Finance; anything else to
     *   Support.
     * @param tripId The **Related trip** selection. Optional by contract, and the refund request
     *   usually has none — a fee charged when the app crashed on Go Online is not about a trip.
     * @param screenshotFileId What [uploadScreenshot] answered.
     */
    suspend fun raise(
        category: String,
        description: String,
        tripId: Ulid? = null,
        screenshotFileId: Ulid? = null,
    ): Ticket = support.createSupportTicket(
        CreateSupportTicketRequest(
            category = category,
            description = description.trim(),
            tripId = tripId,
            screenshotFileId = screenshotFileId,
        ),
    )
}

/**
 * The two ticket categories this screen opens.
 *
 * **Keys, not copy.** `category` is a free-text server key that support-svc derives the queue from,
 * so it is a Kotlin constant for the same reason `Rs` and `L3` are: three identical values in the
 * three `strings.xml` files is what `StringResourceTest` reads as an untranslated key. The
 * driver-facing label beside each is an ordinary string resource.
 */
internal object SupportCategories {

    /** US-9.23 / US-14.11 — *"e.g. app crashed on Go Online"*. Derives `TicketQueue.FINANCE`. */
    const val DAILY_FEE_REFUND: String = "daily_fee_refund"

    /** Everything the *"Raise a ticket"* sheet opens. Derives `TicketQueue.SUPPORT`. */
    const val GENERAL: String = "general"
}
