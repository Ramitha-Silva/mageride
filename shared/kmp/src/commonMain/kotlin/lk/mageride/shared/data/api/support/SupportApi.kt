package lk.mageride.shared.data.api.support

import io.ktor.client.request.parameter
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqListResponse
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail

/**
 * support-svc — the FAQ and the ticket queue (`backend/contracts/support.yaml`, Epic 16).
 *
 * `?lang=` is the trilingual selector (D-26). Leaving it `null` is usually right: the server
 * falls back to the caller's profile language and then to English, and that is one fewer place
 * for the app's idea of the current language to disagree with the profile's.
 */
public interface SupportApi {

    /** `GET /v1/support/faq` — article summaries, optionally narrowed to one category. */
    public suspend fun listFaqArticles(lang: Language? = null, category: String? = null): FaqListResponse

    /** `GET /v1/support/faq/{articleId}` — one article's body. */
    public suspend fun getFaqArticle(articleId: Ulid, lang: Language? = null): FaqArticle

    /** `POST /v1/support/tickets` — raise a ticket, optionally against a trip. */
    public suspend fun createSupportTicket(request: CreateSupportTicketRequest, idempotencyKey: String? = null): Ticket

    /** `GET /v1/support/tickets/{userId}` — this user's tickets, newest first. */
    public suspend fun listSupportTickets(userId: Ulid, page: PageRequest = PageRequest.FIRST): Page<Ticket>

    /** `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket, with the admin's reply. */
    public suspend fun getSupportTicket(userId: Ulid, ticketId: Ulid): TicketDetail
}

internal class KtorSupportApi(private val transport: ApiTransport) : SupportApi {

    override suspend fun listFaqArticles(lang: Language?, category: String?): FaqListResponse =
        transport.apiGet(SERVICE, "listFaqArticles", FAQ_PATH) {
            parameter("lang", lang?.wire)
            parameter("category", category)
        }.decode()

    override suspend fun getFaqArticle(articleId: Ulid, lang: Language?): FaqArticle =
        transport.apiGet(SERVICE, "getFaqArticle", "$FAQ_PATH/$articleId") {
            parameter("lang", lang?.wire)
        }.decode()

    override suspend fun createSupportTicket(request: CreateSupportTicketRequest, idempotencyKey: String?): Ticket =
        transport.apiPost(SERVICE, "createSupportTicket", TICKETS_PATH, idempotencyKey) {
            jsonBody(request)
        }.decode()

    override suspend fun listSupportTickets(userId: Ulid, page: PageRequest): Page<Ticket> =
        transport.apiGet(SERVICE, "listSupportTickets", "$TICKETS_PATH/$userId") { pageParameters(page) }.decode()

    override suspend fun getSupportTicket(userId: Ulid, ticketId: Ulid): TicketDetail =
        transport.apiGet(SERVICE, "getSupportTicket", "$TICKETS_PATH/$userId/$ticketId").decode()

    private companion object {
        val SERVICE = ApiService.SUPPORT
        const val FAQ_PATH = "/v1/support/faq"
        const val TICKETS_PATH = "/v1/support/tickets"
    }
}
