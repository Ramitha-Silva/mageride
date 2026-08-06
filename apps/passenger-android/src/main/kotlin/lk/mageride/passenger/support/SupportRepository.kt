package lk.mageride.passenger.support

import lk.mageride.passenger.history.HistoryRepository
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.support.SupportApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail

/**
 * Cluster 8's support half — support-svc's FAQ and ticket queue, plus the trips SCR-PA-030a's
 * dropdown attaches to (US-16.1, US-16.2).
 *
 * ### `?lang=` is passed, and on this surface that is deliberate
 *
 * `SupportApi`'s KDoc argues for leaving it `null`: the server falls back to the caller's profile
 * language, *"one fewer place for the app's idea of the current language to disagree with the
 * profile's"*. The driver app takes that advice. **This app cannot**, because AL-26 makes the
 * passenger's language a *device-first* answer: SCR-PA-002 is answered before there is a session,
 * SCR-PA-027 writes the device first and lets the server write fail (`languagePendingSync`), and
 * `PassengerLocale.wrap` is what everything else on screen is drawn in. A passenger reading a
 * Sinhala app whose profile has not caught up would get English articles inside it. So the language
 * the app is *drawing in* is the language the FAQ is asked for, and `null` — before SCR-PA-002 has
 * been answered — still means *"use the profile's"*.
 *
 * ### The trip dropdown is C081's history read, not a second one
 *
 * D2' §SCR-PA-030a names `GET /v1/rides` for the *"past Trip ID dropdown"*, and this app already
 * has that read behind [HistoryRepository] — the same call SCR-PA-022's **Past** tab draws. Opening
 * a second door onto it here would be two readers of one list; the ticket sheet borrows the one
 * that exists.
 */
internal class SupportRepository(private val support: SupportApi, private val history: HistoryRepository) {

    /** `GET /v1/support/faq` — the article summaries behind *"🔍 Search help"* (US-16.1). */
    suspend fun faq(language: Language? = null): List<FaqSummary> = support.listFaqArticles(lang = language).items

    /** `GET /v1/support/faq/{articleId}` — one article's markdown body, for the accordion. */
    suspend fun article(articleId: Ulid, language: Language? = null): FaqArticle =
        support.getFaqArticle(articleId, language)

    /** `GET /v1/support/tickets/{userId}` — *"Your tickets"*. */
    suspend fun tickets(userId: Ulid): List<Ticket> = support.listSupportTickets(userId, PageRequest.FIRST).items

    /**
     * `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket **with its thread**.
     *
     * The thread is what makes a resolution readable rather than a status the passenger has to
     * interpret: it carries every transition and every agent reply, oldest first. The agent's
     * identity is never in it — `TicketEvent.actorRole` is a role, by contract.
     */
    suspend fun ticket(userId: Ulid, ticketId: Ulid): TicketDetail = support.getSupportTicket(userId, ticketId)

    /** `GET /v1/rides/history` — the **Related trip** dropdown's options. */
    suspend fun trips(): List<RideHistoryRow> = history.rides().items

    /**
     * `POST /v1/support/screenshots` — the sheet's 📎 attachment (Δ C053).
     *
     * Two calls rather than one multipart ticket, because that is the contract's shape:
     * `POST /v1/support/tickets` takes an **already-uploaded** `docs.uploads` id. A file is
     * attachable to at most one ticket and only by the user who uploaded it, so an upload whose
     * ticket is never submitted is an orphan the 90-day sweep collects — which is the right trade
     * against holding several megabytes in a view model until Submit.
     */
    suspend fun uploadScreenshot(image: PickedScreenshot): Ulid = support.uploadSupportScreenshot(
        FileUpload(fileName = image.fileName, bytes = image.bytes, contentType = image.mimeType),
    ).fileId

    /**
     * `POST /v1/support/tickets` — SCR-PA-030a's **Submit ticket** (US-16.2).
     *
     * @param category A free-text server key. This app sends [SupportCategories.GENERAL] for every
     *   ticket it opens, which is what derives `TicketQueue.SUPPORT`.
     * @param tripId The **Related trip** selection. Optional by contract, and optional here: a
     *   complaint about the app itself is not about a trip.
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
 * The ticket category this screen opens.
 *
 * **A key, not copy.** `category` is a free-text server key support-svc derives the queue from
 * (`support.tickets.category` carries no CHECK), so it is a Kotlin constant for the same reason `Rs`
 * and `SOS` are: three identical values in the three `strings.xml` files is what
 * `StringResourceTest` reads as a key nobody translated.
 *
 * **One value, unlike the driver's two.** `daily_fee_refund` is US-9.23 and is the *driver's* daily
 * fee; no passenger-facing category on the platform routes to the Finance queue, so SCR-PA-030 has
 * no quick action and every ticket it raises is Support's (US-14.13). The FAQ surface publishes the
 * category list, and a passenger-facing addition to it would land here.
 */
internal object SupportCategories {

    /** Everything the *"Raise a ticket"* sheet opens. Derives `TicketQueue.SUPPORT`. */
    const val GENERAL: String = "general"
}
