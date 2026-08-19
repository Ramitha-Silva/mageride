import Foundation
import MageRideShared

/// Cluster 8's support half — support-svc's FAQ and ticket queue, plus the trips SCR-PI-030a's
/// picker attaches to (US-16.1, US-16.2).
///
/// ### `lang` is passed, and on this surface that is deliberate
///
/// `SupportApi`'s own KDoc argues for leaving it `nil`: the server falls back to the caller's
/// profile language, *"one fewer place for the app's idea of the current language to disagree with
/// the profile's"*, and `apps/driver-ios` takes that advice. **This app cannot**, because AL-26
/// makes the passenger's language a *device-first* answer: SCR-PI-002 is answered before there is a
/// session at all, SCR-PI-027 writes the device first and lets the server write fail
/// (`languagePendingSync`), and ``PassengerLocale`` is what everything else on screen is drawn in. A
/// passenger reading a Sinhala app whose profile has not caught up would get English articles inside
/// it. So the language the app is *drawing in* is the language the FAQ is asked for, and `nil` —
/// before SCR-PI-002 has been answered — still means *"use the profile's"*.
///
/// `apps/passenger-android`'s `SupportRepository` makes the identical split, and the two handoffs
/// record it as the one place the two apps' support seams genuinely differ from the driver's.
///
/// ### The trip picker is C099's history read, not a second one
///
/// D2' §SCR-PI-030a names `GET /v1/rides` for the *"past Trip ID"* picker, and this app already has
/// that read behind ``HistoryRepository`` — the same call SCR-PI-022's **Past** tab draws. Opening a
/// second door onto it here would be two readers of one list; the ticket sheet borrows the one that
/// exists.
///
/// A Swift protocol rather than `SupportApi` itself, for the rule `apps/passenger-ios/CLAUDE.md`
/// states: it is a Kotlin interface with `suspend` methods and Swift can stand in for none of them.
protocol SupportRepository: AnyObject {

    /// `GET /v1/support/faq` — the article summaries behind *"🔍 Search help"* (US-16.1).
    func faq(language: Language?) async throws -> [FaqSummary]

    /// `GET /v1/support/faq/{articleId}` — one article's markdown body, for the accordion.
    func article(articleId: String, language: Language?) async throws -> FaqArticle

    /// `GET /v1/support/tickets/{userId}` — *"Your tickets"*.
    func tickets(userId: String) async throws -> [Ticket]

    /// `GET /v1/support/tickets/{userId}/{ticketId}` — one ticket **with its thread**.
    ///
    /// The thread is what makes a resolution readable rather than a status the passenger has to
    /// interpret: it carries every transition and every agent reply, oldest first. The agent's
    /// identity is never in it — `TicketEvent.actorRole` is a role, by contract.
    func ticket(userId: String, ticketId: String) async throws -> TicketDetail

    /// `GET /v1/rides/history` — the **Related trip** picker's options.
    func trips() async throws -> [RideHistoryRow]

    /// `POST /v1/support/screenshots` — the sheet's 📎 attachment.
    ///
    /// Two calls rather than one multipart ticket, because that is the contract's shape:
    /// `POST /v1/support/tickets` takes an **already-uploaded** `docs.uploads` id. A file is
    /// attachable to at most one ticket and only by the user who uploaded it, so an upload whose
    /// ticket is never submitted is an orphan the 90-day sweep collects.
    func uploadScreenshot(fileName: String, data: Data) async throws -> String

    /// `POST /v1/support/tickets` — SCR-PI-030a's **Submit ticket** (US-16.2).
    ///
    /// - Parameter category: A free-text server key. This app sends ``SupportCategories/general``
    ///   for every ticket it opens, which is what derives `TicketQueue.support`.
    /// - Parameter tripId: The **Related trip** selection. Optional by contract, and optional here:
    ///   a complaint about the app itself is not about a trip.
    /// - Parameter screenshotFileId: What ``uploadScreenshot(fileName:data:)`` answered.
    func raise(
        category: String,
        description: String,
        tripId: String?,
        screenshotFileId: String?
    ) async throws -> Ticket
}

/// ``SupportRepository`` over C013's generated support-svc client, plus C099's history seam.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the Objective-C export — which is
/// why `listFaqArticles` is spelled with both of its arguments and `listSupportTickets` with
/// `PageRequest.companion.FIRST`.
final class ApiSupportRepository: SupportRepository {

    private let support: SupportApi
    private let history: HistoryRepository

    init(support: SupportApi, history: HistoryRepository) {
        self.support = support
        self.history = history
    }

    func faq(language: Language?) async throws -> [FaqSummary] {
        try await support.listFaqArticles(lang: language, category: nil).items
    }

    func article(articleId: String, language: Language?) async throws -> FaqArticle {
        try await support.getFaqArticle(articleId: articleId, lang: language)
    }

    /// **`Page<T>.items` arrives as `[Any]`, and only this read needs the cast.** The export emits
    /// the property as `NSArray<id>` — `Page<T>` keeps its type parameter across the bridge and its
    /// rows do not — so the concrete type is put back here. ``faq(language:)`` above is untouched
    /// because `listFaqArticles` answers a `FaqListResponse`, not a `Page`, and that envelope's
    /// `items` is already typed. `compactMap` rather than `as!` for this target's own reason: a
    /// failed force cast raises an exception Swift cannot catch and the process terminates. Every
    /// row is a `Ticket` by contract, so nothing is dropped.
    func tickets(userId: String) async throws -> [Ticket] {
        try await support.listSupportTickets(userId: userId, page: PageRequest.companion.FIRST)
            .items.compactMap { $0 as? Ticket }
    }

    func ticket(userId: String, ticketId: String) async throws -> TicketDetail {
        try await support.getSupportTicket(userId: userId, ticketId: ticketId)
    }

    func trips() async throws -> [RideHistoryRow] {
        try await history.rides()
    }

    /// Uploads the screenshot, converting the bytes **in Kotlin**.
    ///
    /// `IosCapturedDocumentKt.fileUploadOf`, not a `FileUpload` built here: that type takes a
    /// `ByteArray`, whose only Swift-facing mutator is one Objective-C message per byte, and a
    /// screenshot off a modern handset is a couple of megabytes of them. Kotlin does it with one
    /// `memcpy`. The same helper C100's transfer slip goes through, and it is the
    /// **provenance-free** form on purpose: `POST /v1/support/screenshots` declares no `capturedVia`
    /// part beside the file (AL-43), and a screenshot is picked rather than scanned.
    ///
    /// `data:` takes the `Data` as it is: the helper's Kotlin parameter is an `NSData`, and the
    /// importer bridges an Objective-C `NSData *` parameter to `Data` on the Swift side — so an
    /// `as NSData` here is a cast *away* from what the call wants, not towards it.
    func uploadScreenshot(fileName: String, data: Data) async throws -> String {
        try await support.uploadSupportScreenshot(
            file: IosCapturedDocumentKt.fileUploadOf(
                fileName: fileName,
                data: data,
                contentType: ApiSupportRepository.screenshotContentType
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

    /// What a screenshot is sent as. `PhotosPicker` is asked for `.images` and the transferable is
    /// read as `Data`, so a JPEG is the honest default — the same constant C100's slip carries.
    static let screenshotContentType = "image/jpeg"
}

/// The ticket category this screen opens.
///
/// **A key, not copy.** `category` is a free-text server key support-svc derives the queue from
/// (`support.tickets.category` carries no CHECK), so it is a Swift constant for the same reason `Rs`
/// and `SOS` are: three identical values in the three `Localizable.strings` files is what
/// `LocalizationTests` reads as a key nobody translated.
///
/// **One value, unlike the driver's two.** `daily_fee_refund` is US-9.23 and is the *driver's* daily
/// fee; no passenger-facing category on the platform routes to the Finance queue, so SCR-PI-030 has
/// no quick action and every ticket it raises is Support's (US-14.13). The FAQ surface publishes the
/// category list, and a passenger-facing addition to it would land here.
enum SupportCategories {

    /// Everything the *"Raise a ticket"* sheet opens. Derives `TicketQueue.support`.
    static let general = "general"
}
