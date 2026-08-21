package lk.mageride.shared.data.api.support

import io.ktor.client.call.body
import io.ktor.client.request.parameter
import kotlin.coroutines.cancellation.CancellationException
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.filePart
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.multipartBody
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
import lk.mageride.shared.data.models.support.UploadedScreenshot

/**
 * support-svc — the FAQ and the ticket queue (`backend/contracts/support.yaml`, Epic 16).
 *
 * `?lang=` is the trilingual selector (D-26). Leaving it `null` is usually right: the server
 * falls back to the caller's profile language and then to English, and that is one fewer place
 * for the app's idea of the current language to disagree with the profile's.
 */
public interface SupportApi {

    /** `GET /v1/support/faq` — article summaries, optionally narrowed to one category. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listFaqArticles(lang: Language? = null, category: String? = null): FaqListResponse

    /** `GET /v1/support/faq/{articleId}` — one article's body. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getFaqArticle(articleId: Ulid, lang: Language? = null): FaqArticle

    /** `POST /v1/support/tickets` — raise a ticket, optionally against a trip. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun createSupportTicket(request: CreateSupportTicketRequest, idempotencyKey: String? = null): Ticket

    /** `GET /v1/support/tickets/{userId}` — this user's tickets, newest first. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listSupportTickets(userId: Ulid, page: PageRequest = PageRequest.FIRST): Page<Ticket>

    /** `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket, with the admin's reply. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getSupportTicket(userId: Ulid, ticketId: Ulid): TicketDetail

    /**
     * `POST /v1/support/screenshots` — attach a screenshot to a ticket (Δ C053, US-16.2).
     *
     * `POST /v1/support/tickets` takes an already-uploaded id, so before this the upload itself
     * had no surface anywhere on the platform. One `file` part; the response is the `docs.uploads`
     * id the ticket links. **No public URL is minted** — the user reads it back through the
     * short-lived signed link on the ticket detail.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun uploadSupportScreenshot(file: FileUpload, idempotencyKey: String? = null): UploadedScreenshot

    /**
     * `GET /v1/support/screenshots/{uploadId}` — the image bytes behind a signed link (Δ C053).
     *
     * **Unauthenticated, because the signature is the credential.** The URL goes to an image
     * loader, which carries no bearer, and an access token in a query string is an access token
     * in every proxy log on the way. A bad signature, an expired one and an unknown id all answer
     * `403`, so a forged link tells its author nothing.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getSupportScreenshot(uploadId: Ulid, expires: Long, signature: String): ByteArray
}

internal class KtorSupportApi(private val transport: ApiTransport) : SupportApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun uploadSupportScreenshot(file: FileUpload, idempotencyKey: String?): UploadedScreenshot =
        transport.apiPost(
            service = ApiService.SUPPORT,
            operationId = "uploadSupportScreenshot",
            path = "/v1/support/screenshots",
            idempotencyKey = idempotencyKey,
        ) { multipartBody { filePart("file", file) } }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getSupportScreenshot(uploadId: Ulid, expires: Long, signature: String): ByteArray =
        transport.apiGet(ApiService.SUPPORT, "getSupportScreenshot", "/v1/support/screenshots/$uploadId") {
            parameter("expires", expires)
            parameter("signature", signature)
        }.body()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listFaqArticles(lang: Language?, category: String?): FaqListResponse =
        transport.apiGet(SERVICE, "listFaqArticles", FAQ_PATH) {
            parameter("lang", lang?.wire)
            parameter("category", category)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getFaqArticle(articleId: Ulid, lang: Language?): FaqArticle =
        transport.apiGet(SERVICE, "getFaqArticle", "$FAQ_PATH/$articleId") {
            parameter("lang", lang?.wire)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun createSupportTicket(request: CreateSupportTicketRequest, idempotencyKey: String?): Ticket =
        transport.apiPost(SERVICE, "createSupportTicket", TICKETS_PATH, idempotencyKey) {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listSupportTickets(userId: Ulid, page: PageRequest): Page<Ticket> =
        transport.apiGet(SERVICE, "listSupportTickets", "$TICKETS_PATH/$userId") { pageParameters(page) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getSupportTicket(userId: Ulid, ticketId: Ulid): TicketDetail =
        transport.apiGet(SERVICE, "getSupportTicket", "$TICKETS_PATH/$userId/$ticketId").decode()

    private companion object {
        val SERVICE = ApiService.SUPPORT
        const val FAQ_PATH = "/v1/support/faq"
        const val TICKETS_PATH = "/v1/support/tickets"
    }
}
