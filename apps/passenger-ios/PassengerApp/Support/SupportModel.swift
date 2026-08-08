import Combine
import Foundation
import MageRideShared

/// Which of SCR-PI-030's two overlays is up. At most one, which is why it is an enum.
///
/// `Identifiable` because `.sheet(item:)` is: SwiftUI presents **one** sheet per context and
/// silently drops a second, so the two are one binding rather than two `isPresented` flags — the
/// trap C100 recorded on SCR-PI-025a and `apps/driver-ios` on SCR-DI-022.
enum SupportSheet: String, Identifiable {

    /// **SCR-PI-030a** — issue description, related trip, attach screenshot, submit.
    case raiseTicket

    /// One ticket and its whole thread (US-16.2).
    case ticketThread

    var id: String { rawValue }
}

/// SCR-PI-030's state.
///
/// - Parameters:
///   - search: What is typed in *"🔍 Search help"*.
///   - articles: Every FAQ summary read, unfiltered; ``visibleArticles`` is what is drawn.
///   - expandedArticleId: The accordion row that is open, or `nil` when all are closed.
///   - expandedArticle: That row's fetched body. `nil` while the read is in flight.
///   - tickets: *"Your tickets"*, newest first.
///   - trips: The **Related trip** picker's options.
///   - sheet: Which overlay is up.
///   - description: What the passenger typed into the sheet.
///   - tripId: The selected **Related trip**.
///   - screenshotName: The picked attachment's file name. The bytes are held off the state.
///   - ticket: The open ticket and its thread.
///   - isLoading: The first read is in flight.
///   - isSubmitting: Submit is in flight.
///   - raisedTicketId: The ticket the last successful submit opened.
///   - errorKey: Resolved copy for the last failure.
struct SupportState {

    var search = ""
    var articles: [FaqSummary] = []
    var expandedArticleId: String?
    var expandedArticle: FaqArticle?
    var tickets: [Ticket] = []
    var trips: [RideHistoryRow] = []
    var sheet: SupportSheet?
    var description = ""
    var tripId: String?
    var screenshotName: String?
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

    /// Whether **Submit ticket** is live. A ticket with no description is nothing to act on.
    var canSubmit: Bool {
        !isSubmitting && !description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

/// **SCR-PI-030 · support, and SCR-PI-030a's raise-ticket sheet** (US-16.1, US-16.2).
///
/// **The FAQ is an accordion, not a second screen.** D2' §SCR-PI-030 says *"FAQ accordion"*, the
/// wireframe draws a `＋` on each row and its own `Δ iOS` clause is *"`List` + `DisclosureGroup`"* —
/// so opening one expands it in place, which is why the body is held in state next to the id that is
/// open rather than in a sheet. One at a time: a second row closes the first, because two open
/// bodies push *"Your tickets"* off a 5.4" screen.
///
/// **`apps/driver-ios` puts the article in a sheet and this does not**, and that is the two
/// wireframes disagreeing rather than the two apps: `driver_ios.html`'s SCR-DI-033 draws a search
/// field with no results under it, and this cell draws two `glist` rows each carrying a `＋`. The
/// Android twin is an accordion too, so behaviour and layout agree here.
///
/// **A ticket read is scoped to the passenger.** `GET /v1/support/tickets/{userId}` takes the user in
/// the path, so the id comes from the session rather than from anything a screen holds — and a
/// signed-out passenger simply has no ticket list, while the FAQ still works.
///
/// **The screenshot is uploaded by Submit, not by the picker.** Two calls are the contract's shape
/// (the ticket takes an already-uploaded id), and doing the upload on Submit means a passenger who
/// opens the sheet, attaches a photo and changes their mind has cost the platform nothing. A failed
/// upload does **not** stop the ticket: what they wrote is the part support acts on.
@MainActor
final class SupportModel: ObservableObject {

    @Published private(set) var state = SupportState()

    private let support: SupportRepository
    private let sessions: PassengerSessions
    private let preferences: AppPreferences

    /// The attached screenshot's bytes.
    ///
    /// Held **off** ``state`` for the reason C100's ``SubscriptionPayModel`` holds its transfer slip
    /// there: a couple of megabytes of `Data` on an `@Published` value is copied on every mutation
    /// of any other field, and the sheet mutates `description` on every keystroke.
    private var screenshot: Data?

    init(support: SupportRepository, sessions: PassengerSessions, preferences: AppPreferences) {
        self.support = support
        self.sessions = sessions
        self.preferences = preferences
    }

    /// Reads the FAQ and this passenger's tickets.
    ///
    /// The FAQ is committed **before** the ticket read runs, which is what makes a failure of the
    /// second leave the first on screen: *"search help"* still works for a passenger whose ticket
    /// list could not be read.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil

        // AL-26: the language the app is DRAWING in, not the profile's. See ``SupportRepository``.
        state.articles = (try? await support.faq(language: preferences.language)) ?? []

        guard let userId = sessions.userId else {
            state.isLoading = false
            return
        }

        do {
            state.tickets = try await support.tickets(userId: userId)
        } catch {
            state.errorKey = SupportErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// The *"🔍 Search help"* field. Filters what has been read; sends nothing.
    func onSearchChange(_ raw: String) {
        state.search = raw
    }

    /// A row of the accordion (US-16.1).
    ///
    /// Tapping the open row closes it. Opening a different one replaces both the id and the body, so
    /// the previous article's text can never be drawn under the new title while its read is in
    /// flight.
    func toggleArticle(articleId: String) async {
        guard state.expandedArticleId != articleId else {
            state.expandedArticleId = nil
            state.expandedArticle = nil
            return
        }

        state.expandedArticleId = articleId
        state.expandedArticle = nil
        state.errorKey = nil

        do {
            let article = try await support.article(articleId: articleId, language: preferences.language)
            // Guarded: a slow read for a row the passenger has since closed must not re-open it.
            guard state.expandedArticleId == articleId else { return }
            state.expandedArticle = article
        } catch {
            if state.expandedArticleId == articleId { state.expandedArticleId = nil }
            state.errorKey = SupportErrors.messageKey(for: error)
        }
    }

    /// Opens SCR-PI-030a with an empty draft.
    func openTicketSheet() {
        state.sheet = .raiseTicket
        state.description = ""
        state.tripId = nil
        state.screenshotName = nil
        state.raisedTicketId = nil
        state.errorKey = nil
        screenshot = nil
        Task { [weak self] in await self?.readTrips() }
    }

    /// Opens one ticket's thread.
    func openTicket(ticketId: String) async {
        guard let userId = sessions.userId else { return }
        state.sheet = .ticketThread
        state.ticket = nil
        state.errorKey = nil

        do {
            state.ticket = try await support.ticket(userId: userId, ticketId: ticketId)
        } catch {
            state.sheet = nil
            state.errorKey = SupportErrors.messageKey(for: error)
        }
    }

    /// Takes whichever overlay is up back down.
    func closeSheet() {
        state.sheet = nil
        state.ticket = nil
    }

    /// SCR-PI-030a's **Issue description**.
    func onDescriptionChange(_ raw: String) {
        state.description = raw
        state.errorKey = nil
    }

    /// SCR-PI-030a's **Related trip** picker. `nil` clears the selection.
    func onTripSelected(_ tripId: String?) {
        state.tripId = tripId
    }

    /// SCR-PI-030a's 📎. The bytes are held until Submit; nothing is sent yet.
    func onScreenshotPicked(fileName: String, data: Data) {
        screenshot = data
        state.screenshotName = fileName
    }

    /// **Submit ticket** — the screenshot first, then the ticket that links it (US-16.2).
    ///
    /// A failed screenshot upload does **not** stop the ticket: what the passenger wrote is the part
    /// support acts on, and losing a complaint because an image did not go up would be the wrong
    /// trade. The attachment is simply absent, and the ticket says everything else.
    func submit() async {
        guard state.canSubmit else { return }
        state.isSubmitting = true
        state.errorKey = nil

        var fileId: String?
        if let data = screenshot {
            fileId = try? await support.uploadScreenshot(
                fileName: state.screenshotName ?? Self.screenshotFileName,
                data: data
            )
        }

        do {
            let raised = try await support.raise(
                category: SupportCategories.general,
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
            state.screenshotName = nil
            screenshot = nil
        } catch {
            state.errorKey = SupportErrors.messageKey(for: error)
        }
        state.isSubmitting = false
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// The **Related trip** options, read once per sheet opening.
    private func readTrips() async {
        guard state.trips.isEmpty else { return }
        state.trips = (try? await support.trips()) ?? []
    }

    /// What the attachment is called in `docs.uploads` when the picker exposed no name. Not
    /// user-facing, and deliberately not the file's own name: `IObjectStore`'s key is built from ids
    /// the platform minted, never from a client filename (backend/CLAUDE.md).
    private static let screenshotFileName = "support-screenshot.jpg"
}
