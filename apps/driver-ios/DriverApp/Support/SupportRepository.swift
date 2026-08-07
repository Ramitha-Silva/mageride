import Foundation
import MageRideShared

/// SCR-DI-033's data — support-svc's FAQ and ticket queue, plus the trips the ticket sheet attaches
/// to (US-16.1/16.2, US-9.23).
///
/// ### The daily-fee refund is a CATEGORY, not an endpoint
///
/// The wireframe's *"Request daily-fee refund"* quick action and its *"Raise a ticket"* sheet post to
/// the **same** `POST /v1/support/tickets`; what differs is `category`. `SupportModels` is explicit
/// about why — *"`support.tickets.category` carries no CHECK, and the driver daily-fee refund request
/// (US-9.23) is deliberately modelled as an ordinary category rather than its own endpoint"* — and
/// support-svc derives the queue from it, so `daily_fee_refund` routes to **Finance** (US-14.11) and
/// everything else to Support. A second route would have been a second way to open the same row.
///
/// ### The trip dropdown is the ride-history read model, not a second one
///
/// *"Related trip · Galle Face → Nugegoda"* is `GET /v1/trips/{driverId}` — the same call SCR-DI-030
/// draws its list from (C092), and the only read that spans both planes for a driver. It is reached
/// through this repository rather than through ``RideHistoryRepository`` because that protocol's
/// other three methods are the rating door and this screen must not grow one.
///
/// ### `lang` is deliberately left `nil`
///
/// `SupportApi`'s own note: the server falls back to the caller's profile language and then to
/// English, *"and that is one fewer place for the app's idea of the current language to disagree
/// with the profile's"*. SCR-DI-029 can change the interface language without a round trip
/// (``DriverLocale`` redirects the bundle), which makes the app's idea the *more* likely of the two
/// to be ahead — so the server's is the one to trust for server-rendered prose.
///
/// A protocol for the reason every seam in this target is one: `SupportApi` and `QueryApi` are
/// Kotlin interfaces and cannot be stood in for from Swift.
@MainActor
protocol SupportRepository: AnyObject {

    /// `GET /v1/support/faq` — the article summaries behind *"🔍 Search help"* (US-16.1).
    func faq() async throws -> [FaqSummary]

    /// `GET /v1/support/faq/{articleId}` — one article's markdown body.
    func article(articleId: String) async throws -> FaqArticle

    /// `GET /v1/support/tickets/{userId}` — *"Your tickets"*, newest first.
    func tickets(userId: String) async throws -> [Ticket]

    /// `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket **with its thread**.
    ///
    /// The thread is what makes a resolution readable rather than a status the driver has to
    /// interpret: it carries every transition and every agent reply, oldest first. The agent's
    /// identity is never in it — `TicketEvent.actorRole` is a role, by contract.
    func ticket(userId: String, ticketId: String) async throws -> TicketDetail

    /// `GET /v1/trips/{driverId}` — the **Related trip** picker's options.
    func trips(driverId: String) async throws -> [TripSummary]

    /// `POST /v1/support/screenshots` — the sheet's 📎 attachment (Δ C053).
    ///
    /// Two calls rather than one multipart ticket, because that is the contract's shape:
    /// `POST /v1/support/tickets` takes an **already-uploaded** `docs.uploads` id. A file is
    /// attachable to at most one ticket and only by the user who uploaded it, so an upload whose
    /// ticket is never submitted is an orphan the 90-day sweep collects — which is the right trade
    /// against holding several megabytes in a model until Submit.
    func uploadScreenshot(_ image: CapturedImage) async throws -> String

    /// `POST /v1/support/tickets` — SCR-DI-033a's Submit, and the refund quick action (US-16.2).
    ///
    /// - Parameters:
    ///   - category: ``SupportCategories/dailyFeeRefund`` routes to Finance; anything else to Support.
    ///   - tripId: The **Related trip** selection. Optional by contract, and the refund request
    ///     usually has none — a fee charged when the app crashed on Go Online is not about a trip.
    ///   - screenshotFileId: What ``uploadScreenshot(_:)`` answered.
    func raise(
        category: String,
        description: String,
        tripId: String?,
        screenshotFileId: String?
    ) async throws -> Ticket
}

/// ``SupportRepository`` over `:shared`'s support and query clients.
@MainActor
final class ApiSupportRepository: SupportRepository {

    private let support: SupportApi
    private let query: QueryApi

    init(support: SupportApi, query: QueryApi) {
        self.support = support
        self.query = query
    }

    func faq() async throws -> [FaqSummary] {
        try await support.listFaqArticles(lang: nil, category: nil).items
    }

    func article(articleId: String) async throws -> FaqArticle {
        try await support.getFaqArticle(articleId: articleId, lang: nil)
    }

    func tickets(userId: String) async throws -> [Ticket] {
        try await support.listSupportTickets(userId: userId, page: PageRequest.companion.FIRST).items
    }

    func ticket(userId: String, ticketId: String) async throws -> TicketDetail {
        try await support.getSupportTicket(userId: userId, ticketId: ticketId)
    }

    func trips(driverId: String) async throws -> [TripSummary] {
        try await query.listTrips(userId: driverId, page: PageRequest.companion.FIRST).items
    }

    func uploadScreenshot(_ image: CapturedImage) async throws -> String {
        // `IosCapturedDocumentKt.fileUploadOf`, not a `FileUpload` built here: `FileUpload` takes a
        // `ByteArray`, whose only Swift-facing mutator is one Objective-C message per byte, and a
        // screenshot is a megabyte of them. Kotlin does the copy with one `memcpy` (Δ C093).
        //
        // The bare form rather than `capturedDocument`, deliberately: a support screenshot is not a
        // document and `POST /v1/support/screenshots` declares no `…CapturedVia` part beside it, so
        // AL-43's provenance stamp is dropped here for the same reason C089 drops it on the delivery
        // proof — the Verification-Officer queue is not where this file lands.
        try await support.uploadSupportScreenshot(
            file: IosCapturedDocumentKt.fileUploadOf(
                fileName: image.fileName,
                data: image.data as NSData,
                contentType: image.mimeType
            ),
            idempotencyKey: nil
        ).fileId
    }

    func raise(
        category: String,
        description: String,
        tripId: String?,
        screenshotFileId: String?
    ) async throws -> Ticket {
        try await support.createSupportTicket(
            request: CreateSupportTicketRequest(
                category: category,
                description: description.trimmingCharacters(in: .whitespacesAndNewlines),
                tripId: tripId,
                screenshotFileId: screenshotFileId
            ),
            idempotencyKey: nil
        )
    }
}

/// The two ticket categories this screen opens.
///
/// **Keys, not copy.** `category` is a free-text server key that support-svc derives the queue from,
/// so it is a Swift constant for the same reason `Rs` and `SOS` are: three identical values in the
/// three `Localizable.strings` files is what `LocalizationTests` reads as a key nobody translated.
/// The driver-facing label beside each is an ordinary string key — see ``SupportLabels``.
enum SupportCategories {

    /// US-9.23 / US-14.11 — *"e.g. app crashed on Go Online"*. Derives `TicketQueue.finance`.
    static let dailyFeeRefund = "daily_fee_refund"

    /// Everything the *"Raise a ticket"* sheet opens. Derives `TicketQueue.support`.
    static let general = "general"
}
