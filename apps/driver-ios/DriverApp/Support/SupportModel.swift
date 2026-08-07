import Combine
import Foundation
import MageRideShared

/// Which of SCR-DI-033's three overlays is up. At most one, which is why it is an enum.
///
/// `Identifiable` because `.sheet(item:)` is: SwiftUI presents **one** sheet per context and
/// silently drops a second, so the three are one binding rather than three `isPresented` flags —
/// the trap C091 recorded on SCR-DI-022 and the reason that screen ranks its own three.
enum SupportSheet: String, Identifiable {

    /// **SCR-DI-033a** — issue description, related trip, attach screenshot, submit.
    case raiseTicket

    /// One ticket and its whole thread (US-16.2).
    case ticketThread

    /// One FAQ article's body (US-16.1).
    case faqArticle

    var id: String { rawValue }
}

/// SCR-DI-033's state.
///
/// - Parameters:
///   - search: What is typed in *"🔍 Search help"*.
///   - articles: Every FAQ summary read, unfiltered; ``visibleArticles`` is what is drawn.
///   - tickets: *"Your tickets"*, newest first.
///   - trips: The **Related trip** picker's options.
///   - sheet: Which overlay is up.
///   - draftCategory: Which category ``SupportSheet/raiseTicket`` will post — the quick action fixes
///     it to ``SupportCategories/dailyFeeRefund``, the CTA leaves it general.
///   - description: What the driver typed into the sheet.
///   - tripId: The selected **Related trip**.
///   - screenshot: The picked attachment, held until Submit uploads it.
///   - article: The open FAQ article's body.
///   - ticket: The open ticket and its thread.
///   - isLoading: The first read is in flight.
///   - isSubmitting: Submit is in flight.
///   - raisedTicketId: The ticket the last successful submit opened.
///   - errorKey: Resolved copy for the last failure.
struct SupportState {

    var search = ""
    var articles: [FaqSummary] = []
    var tickets: [Ticket] = []
    var trips: [TripSummary] = []
    var sheet: SupportSheet?
    var draftCategory = SupportCategories.general
    var description = ""
    var tripId: String?
    var screenshot: CapturedImage?
    var article: FaqArticle?
    var ticket: TicketDetail?
    var isLoading = true
    var isSubmitting = false
    var raisedTicketId: String?
    var errorKey: String?

    /// The articles the search box leaves.
    ///
    /// Filtered **on the device**, over titles already read. `GET /v1/support/faq` takes a `category`
    /// and no query string, so there is no server-side search to defer to; typing a word and getting
    /// a request per keystroke would be worse than filtering a list that is a few dozen rows long.
    var visibleArticles: [FaqSummary] {
        let needle = search.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !needle.isEmpty else { return articles }
        return articles.filter { $0.title.localizedCaseInsensitiveContains(needle) }
    }

    /// Whether the FAQ results are drawn at all.
    ///
    /// Only while something is typed: the wireframe's resting state is the search field with the
    /// quick actions under it, and a full FAQ index pushed above *"Request daily-fee refund"* would
    /// bury the one action a driver opens this screen for.
    var isSearching: Bool { !search.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    /// Whether **Submit ticket** is live. A ticket with no description is nothing to act on.
    var canSubmit: Bool {
        !isSubmitting && !description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Whether the sheet is the daily-fee refund rather than a general ticket (US-9.23).
    var isRefundRequest: Bool { draftCategory == SupportCategories.dailyFeeRefund }
}

/// **SCR-DI-033 · support and the daily-fee refund** (US-16.1/16.2, US-9.23, US-14.11).
///
/// **The refund request and *"Raise a ticket"* are ONE flow with two categories.** Both open
/// SCR-DI-033a and both post `POST /v1/support/tickets`; the quick action fixes
/// `category=daily_fee_refund`, which is what routes the row to Finance rather than Support. There
/// is no refund endpoint to call — see ``SupportRepository`` — and inventing a second screen for a
/// second value of one field would have been two ways to open the same ticket.
///
/// **The screenshot is uploaded by Submit, not by the picker.** Two calls are the contract's shape
/// (the ticket takes an already-uploaded id), and doing the upload on Submit is the same choice C089
/// made for the delivery proof: a driver who opens the sheet, attaches a photo and changes their
/// mind has cost the platform nothing.
///
/// **A ticket read is scoped to the driver.** `GET /v1/support/tickets/{userId}` takes the user in
/// the path, so the id comes from ``DriverIdentity`` rather than from anything the screen holds.
@MainActor
final class SupportModel: ObservableObject {

    @Published private(set) var state = SupportState()

    private let identity: DriverIdentity
    private let support: SupportRepository

    init(identity: DriverIdentity, support: SupportRepository) {
        self.identity = identity
        self.support = support
    }

    /// Reads the FAQ and this driver's tickets.
    ///
    /// The FAQ is public-ish and the tickets are not, so a failure to resolve the driver leaves the
    /// articles up rather than emptying the screen: *"search help"* still works for a driver whose
    /// ticket list could not be read.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil

        // Folded in BEFORE the ticket read rather than after both, which is what makes the sentence
        // above true: a ticket list that could not be read must leave the articles up.
        state.articles = (try? await support.faq()) ?? []

        guard let driverId = identity.driverId else {
            state.isLoading = false
            return
        }

        do {
            state.tickets = try await support.tickets(userId: driverId)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// Opens SCR-DI-033a.
    ///
    /// - Parameter category: ``SupportCategories/dailyFeeRefund`` from the quick action's
    ///   *"Request"*, ``SupportCategories/general`` from the *"Raise a ticket"* CTA.
    func openTicketSheet(category: String) {
        state.sheet = .raiseTicket
        state.draftCategory = category
        state.description = ""
        state.tripId = nil
        state.screenshot = nil
        state.raisedTicketId = nil
        state.errorKey = nil
        Task { [weak self] in await self?.readTrips() }
    }

    /// Opens one ticket's thread.
    func openTicket(ticketId: String) async {
        guard let driverId = identity.driverId else { return }
        state.sheet = .ticketThread
        state.ticket = nil
        state.errorKey = nil

        do {
            state.ticket = try await support.ticket(userId: driverId, ticketId: ticketId)
        } catch {
            state.sheet = nil
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// Opens one FAQ article's body (US-16.1).
    func openArticle(articleId: String) async {
        state.sheet = .faqArticle
        state.article = nil
        state.errorKey = nil

        do {
            state.article = try await support.article(articleId: articleId)
        } catch {
            state.sheet = nil
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// Takes whichever overlay is up back down.
    func closeSheet() {
        state.sheet = nil
        state.article = nil
        state.ticket = nil
    }

    /// The *"🔍 Search help"* field. Filters what has been read; sends nothing.
    func onSearchChange(_ raw: String) {
        state.search = raw
    }

    /// SCR-DI-033a's **Issue description**.
    func onDescriptionChange(_ raw: String) {
        state.description = raw
        state.errorKey = nil
    }

    /// SCR-DI-033a's **Related trip** picker. `nil` clears the selection.
    func onTripSelected(_ tripId: String?) {
        state.tripId = tripId
    }

    /// SCR-DI-033a's 📎. The bytes are held until Submit; nothing is sent yet.
    func onScreenshotPicked(_ image: CapturedImage?) {
        state.screenshot = image
    }

    /// **Submit ticket** — the screenshot first, then the ticket that links it (US-16.2).
    ///
    /// A failed screenshot upload does **not** stop the ticket: what the driver wrote is the part
    /// support acts on, and losing a complaint because an image did not go up would be the wrong
    /// trade. The attachment is simply absent, and the ticket says everything else.
    func submit() async {
        guard state.canSubmit else { return }
        state.isSubmitting = true
        state.errorKey = nil

        let fileId: String?
        if let screenshot = state.screenshot {
            fileId = try? await support.uploadScreenshot(screenshot)
        } else {
            fileId = nil
        }

        do {
            let raised = try await support.raise(
                category: state.draftCategory,
                description: state.description,
                tripId: state.tripId,
                screenshotFileId: fileId
            )

            // Prepended rather than re-read: `POST` answers with the row, and a list re-read would
            // be a round trip to learn what the response already said.
            state.tickets.insert(raised, at: 0)
            state.raisedTicketId = raised.ticketId
            state.sheet = nil
            state.description = ""
            state.tripId = nil
            state.screenshot = nil
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isSubmitting = false
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// The **Related trip** options, read once per sheet opening.
    private func readTrips() async {
        guard state.trips.isEmpty, let driverId = identity.driverId else { return }
        state.trips = (try? await support.trips(driverId: driverId)) ?? []
    }
}
