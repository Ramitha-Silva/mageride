package lk.mageride.shared.data.models.support

import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// support-svc — FAQ articles and in-app tickets.
// Source: backend/contracts/support.yaml (D3' "support-svc — FAQ & tickets", Epic 16,
// ADD Appendix C).
//
// FAQ articles exist in Sinhala, Tamil and English (US-16.1, D-26).
//
// `category` is a FREE-TEXT KEY rather than an enum — support.tickets.category carries no CHECK,
// and the driver daily-fee refund request (US-9.23) is deliberately modelled as an ordinary
// category rather than its own endpoint (C005). Clients pick from the category list the FAQ
// surface publishes; an enum here would have to be revised every time Support adds a topic.

/**
 * A reference to a newly opened support ticket.
 *
 * Both `POST /v1/rides/{rideId}/dispute` (E-05) and `POST /v1/fare/pay/driver-qr/dispute`
 * (AL-47) answer with this identical `{ ticketId }` body. One type, because it is one shape —
 * and it is a support ticket, so it lives here.
 *
 * @property ticketId The opened ticket; readable through `GET /v1/support/tickets/{userId}`.
 */
@Serializable
public data class TicketRef(val ticketId: Ulid)

/** Where a support ticket stands (`support.tickets.status` CHECK, C005). */
@Serializable
public enum class TicketStatus {
    OPEN,
    IN_PROGRESS,
    RESOLVED,
}

/**
 * An FAQ article summary (`support.yaml#/components/schemas/FaqSummary`, US-16.1).
 *
 * @property articleId The article.
 * @property title Server-rendered in [language]; not a string this module owns.
 * @property category The topic key it is filed under.
 * @property language Which of the three languages this row is.
 */
@Serializable
public data class FaqSummary(val articleId: Ulid, val title: String, val category: String, val language: Language)

/**
 * A full FAQ article (`support.yaml#/components/schemas/FaqArticle` —
 * `allOf(FaqSummary, { body })`, flattened).
 *
 * Falls back to English only if the requested language has no row for the article.
 *
 * @property body Markdown, in [language].
 */
@Serializable
public data class FaqArticle(
    val articleId: Ulid,
    val title: String,
    val category: String,
    val language: Language,
    val body: String,
)

/**
 * `GET /v1/support/faq` — 200.
 *
 * @property items Article summaries; the body is fetched per article.
 */
@Serializable
public data class FaqListResponse(val items: List<FaqSummary> = emptyList())

/**
 * `POST /v1/support/tickets` (US-16.2).
 *
 * @property category A category key from the FAQ surface, e.g. `daily_fee_refund` (US-9.23).
 * @property description User-written, at most 4000 characters.
 * @property tripId Attaches a Mode C ride or a Mode A/B session.
 * @property screenshotFileId An already-uploaded `docs.uploads` id.
 */
@Serializable
public data class CreateSupportTicketRequest(
    val category: String,
    val description: String,
    val tripId: Ulid? = null,
    val screenshotFileId: Ulid? = null,
)

/**
 * A support ticket (`support.yaml#/components/schemas/Ticket`).
 *
 * @property ticketId The ticket.
 * @property category The topic key.
 * @property status Where it stands. Tickets open in [TicketStatus.OPEN].
 * @property tripId The attached trip, when there is one.
 * @property createdAt When it was raised.
 * @property resolvedAt When an agent closed it.
 */
@Serializable
public data class Ticket(
    val ticketId: Ulid,
    val category: String,
    val status: TicketStatus,
    val tripId: Ulid? = null,
    val createdAt: Timestamp,
    val resolvedAt: Timestamp? = null,
)

/**
 * A ticket with its conversation (`support.yaml#/components/schemas/TicketDetail` —
 * `allOf(Ticket, …)`, flattened).
 *
 * @property description What the user wrote.
 * @property screenshotUrl Short-lived signed object-storage URL.
 * @property adminResponse The agent's reply, once there is one.
 */
@Serializable
public data class TicketDetail(
    val ticketId: Ulid,
    val category: String,
    val status: TicketStatus,
    val tripId: Ulid? = null,
    val createdAt: Timestamp,
    val resolvedAt: Timestamp? = null,
    val description: String,
    val screenshotUrl: String? = null,
    val adminResponse: String? = null,
)
