import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-033 / 033a · support and the daily-fee refund** — one flow, two categories, and the
/// trade-offs the two reads and the two writes make.
@MainActor
final class SupportModelTests: XCTestCase {

    private var identity = FakeDriverIdentity()
    private var support = FakeSupportRepository()

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        support = FakeSupportRepository()
    }

    private func makeModel() -> SupportModel {
        SupportModel(identity: identity, support: support)
    }

    private func settle() async {
        for _ in 0..<8 { await Task.yield() }
    }

    // MARK: - US-9.23 · the refund is a category, not an endpoint

    /// **Both entry points post the same operation.** `support.tickets.category` carries no CHECK and
    /// support-svc derives the queue from it, so `daily_fee_refund` routes to Finance and everything
    /// else to Support. A second route would have been a second way to open the same row.
    func testTheRefundQuickActionAndTheCtaPostTheSameOperationWithDifferentCategories() async {
        let model = makeModel()

        model.openTicketSheet(category: SupportCategories.dailyFeeRefund)
        model.onDescriptionChange("Fee charged when the app crashed on Go Online")
        await model.submit()

        model.openTicketSheet(category: SupportCategories.general)
        model.onDescriptionChange("The map will not load")
        await model.submit()

        XCTAssertEqual(
            support.raised.map(\.category),
            [SupportCategories.dailyFeeRefund, SupportCategories.general]
        )
    }

    /// The sheet's own title follows the category, because it is the same sheet.
    func testTheSheetKnowsWhetherItIsARefundRequest() {
        let model = makeModel()

        model.openTicketSheet(category: SupportCategories.dailyFeeRefund)
        XCTAssertTrue(model.state.isRefundRequest)

        model.openTicketSheet(category: SupportCategories.general)
        XCTAssertFalse(model.state.isRefundRequest)
    }

    /// Opening the sheet clears the last draft — a description left behind from a ticket the driver
    /// already sent would be submitted twice.
    func testOpeningTheSheetClearsTheLastDraft() async {
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.general)
        model.onDescriptionChange("first")
        await model.submit()

        model.openTicketSheet(category: SupportCategories.general)

        XCTAssertEqual(model.state.description, "")
        XCTAssertNil(model.state.tripId)
        XCTAssertNil(model.state.screenshot)
        XCTAssertNil(model.state.raisedTicketId)
    }

    // MARK: - The first read

    /// The FAQ is public-ish and the tickets are not, so a failure to read the tickets leaves the
    /// articles up: *"search help"* still works for a driver whose ticket list could not be read.
    func testAFailedTicketReadLeavesTheArticlesUp() async {
        support.articles = [faqSummary()]
        support.ticketsFailure = CancellationError()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.articles.count, 1)
        XCTAssertNotNil(model.state.errorKey)
        XCTAssertFalse(model.state.isLoading)
    }

    /// A driver whose session could not be resolved still gets the FAQ half of the screen.
    func testTheArticlesAreReadEvenWithNoDriverId() async {
        identity.driverId = nil
        support.articles = [faqSummary()]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.articles.count, 1)
        XCTAssertEqual(support.ticketReads, 0, "a ticket read is scoped to a user in the path")
    }

    // MARK: - US-16.1 · the search

    /// **Filtered on the device.** `GET /v1/support/faq` takes a `category` and no query string, so
    /// there is no server-side search to defer to; a request per keystroke would be worse than
    /// filtering a list a few dozen rows long.
    func testSearchFiltersWhatWasAlreadyReadAndSendsNothing() async {
        support.articles = [
            faqSummary(articleId: "a1", title: "How the daily fee works"),
            faqSummary(articleId: "a2", title: "Adding a vehicle"),
        ]
        let model = makeModel()
        await model.refresh()

        model.onSearchChange("vehicle")

        XCTAssertEqual(model.state.visibleArticles.map(\.articleId), ["a2"])
        XCTAssertEqual(support.faqReads, 1, "typing sends nothing")
    }

    func testSearchIsCaseInsensitiveAndIgnoresSurroundingSpace() async {
        support.articles = [faqSummary(articleId: "a1", title: "How the daily fee works")]
        let model = makeModel()
        await model.refresh()

        model.onSearchChange("  DAILY  ")

        XCTAssertEqual(model.state.visibleArticles.count, 1)
    }

    /// The wireframe's resting state is the search field with the quick actions under it; a full FAQ
    /// index pushed above *"Request daily-fee refund"* would bury the one action a driver opens this
    /// screen for.
    func testTheResultsAreDrawnOnlyWhileSomethingIsTyped() async {
        support.articles = [faqSummary()]
        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.isSearching)
        model.onSearchChange("   ")
        XCTAssertFalse(model.state.isSearching, "whitespace is not a search")
        model.onSearchChange("fee")
        XCTAssertTrue(model.state.isSearching)
    }

    // MARK: - US-16.2 · submit

    /// A ticket with no description is nothing to act on.
    func testSubmitRefusesAnEmptyDescription() async {
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.general)
        model.onDescriptionChange("   ")

        XCTAssertFalse(model.state.canSubmit)
        await model.submit()

        XCTAssertTrue(support.raised.isEmpty)
    }

    /// **The screenshot is uploaded by Submit, not by the picker** — two calls are the contract's
    /// shape, and a driver who attaches a photo and changes their mind has cost the platform nothing.
    func testTheScreenshotIsUploadedBySubmitAndItsIdIsLinked() async {
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.general)
        model.onScreenshotPicked(CapturedImage(fileName: "shot.jpg", data: Data([0x1])))

        XCTAssertTrue(support.uploads.isEmpty, "picking uploads nothing")

        model.onDescriptionChange("The map will not load")
        await model.submit()

        XCTAssertEqual(support.uploads.count, 1)
        XCTAssertEqual(support.raised.first?.fileId, support.uploadedFileId)
    }

    /// **A failed screenshot upload does not stop the ticket.** What the driver wrote is the part
    /// support acts on; losing a complaint because an image did not go up would be the wrong trade.
    func testAFailedUploadStillRaisesTheTicketWithoutTheAttachment() async {
        support.uploadFailure = CancellationError()
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.general)
        model.onScreenshotPicked(CapturedImage(fileName: "shot.jpg", data: Data([0x1])))
        model.onDescriptionChange("The map will not load")

        await model.submit()

        XCTAssertEqual(support.raised.count, 1)
        XCTAssertNil(support.raised.first?.fileId)
    }

    /// Prepended rather than re-read: `POST` answers with the row, and a list re-read would be a
    /// round trip to learn what the response already said.
    func testASubmittedTicketIsPrependedRatherThanRefetched() async {
        support.storedTickets = [supportTicket(ticketId: "older")]
        let model = makeModel()
        await model.refresh()
        let readsBefore = support.ticketReads

        model.openTicketSheet(category: SupportCategories.general)
        model.onDescriptionChange("The map will not load")
        await model.submit()

        XCTAssertEqual(model.state.tickets.count, 2)
        XCTAssertEqual(model.state.tickets.last?.ticketId, "older")
        XCTAssertEqual(support.ticketReads, readsBefore, "no second list read")
        XCTAssertNil(model.state.sheet, "the sheet closes on success")
        XCTAssertNotNil(model.state.raisedTicketId)
    }

    func testAFailedSubmitKeepsTheSheetUpWithItsCopy() async {
        support.raiseFailure = CancellationError()
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.general)
        model.onDescriptionChange("The map will not load")

        await model.submit()

        XCTAssertEqual(model.state.sheet, .raiseTicket)
        XCTAssertNotNil(model.state.errorKey)
        XCTAssertFalse(model.state.isSubmitting)
    }

    // MARK: - The Related trip picker

    /// Read once per sheet opening, and not again — the list does not change while a ticket is typed.
    func testTheTripOptionsAreReadOnceAndReused() async {
        support.storedTrips = [tripSummary()]
        let model = makeModel()

        model.openTicketSheet(category: SupportCategories.general)
        await settle()
        model.openTicketSheet(category: SupportCategories.dailyFeeRefund)
        await settle()

        XCTAssertEqual(support.tripReads, 1)
        XCTAssertEqual(model.state.trips.count, 1)
    }

    /// Optional by contract, and the refund request usually has none — a fee charged when the app
    /// crashed on Go Online is not about a trip.
    func testATicketWithNoTripIsSentWithoutOne() async {
        let model = makeModel()
        model.openTicketSheet(category: SupportCategories.dailyFeeRefund)
        model.onDescriptionChange("Fee charged in error")

        await model.submit()

        XCTAssertNil(support.raised.first?.tripId)
    }

    // MARK: - The label tables

    /// **`category` is a free-text server key and cannot be an exhaustive table.** A key this build
    /// does not know is rendered from the key itself rather than collapsed into *"Support request"*,
    /// because a driver looking at their own ticket list needs to tell two of them apart.
    func testAnUnknownCategoryIsMadeLegibleRatherThanCollapsed() {
        XCTAssertNil(SupportLabels.categoryKey("fare_dispute"))
        XCTAssertEqual(SupportLabels.category("fare_dispute"), "Fare dispute")
        XCTAssertEqual(SupportLabels.categoryKey(SupportCategories.dailyFeeRefund), "support_category_refund")
        XCTAssertEqual(SupportLabels.categoryKey(SupportCategories.general), "support_category_general")
    }

    /// One table for the list row and the thread header alike: the same ticket wearing two colours on
    /// two surfaces is the kind of thing a driver reads as two different tickets.
    func testEveryTicketStatusHasOneLabelAndOneTone() {
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.open), "support_status_open")
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.inProgress), "support_status_in_progress")
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.resolved), "support_status_resolved")
        XCTAssertEqual(SupportLabels.tone(TicketStatus.open), .pending)
        XCTAssertEqual(SupportLabels.tone(TicketStatus.inProgress), .info)
        XCTAssertEqual(SupportLabels.tone(TicketStatus.resolved), .done)
    }

    /// `assigned` is in the enum and is **never returned to a user** — who is handling a complaint is
    /// not theirs — so it has no copy and the thread skips it rather than printing an empty row.
    func testTheAssignedThreadEntryHasNoCopyAndIsSkipped() {
        XCTAssertNil(SupportLabels.eventKey(TicketEventKind.assigned))
        XCTAssertEqual(SupportLabels.eventKey(TicketEventKind.opened), "support_event_opened")
        XCTAssertEqual(SupportLabels.eventKey(TicketEventKind.responded), "support_event_responded")
        XCTAssertEqual(SupportLabels.eventKey(TicketEventKind.resolved), "support_event_resolved")
        XCTAssertEqual(SupportLabels.eventKey(TicketEventKind.reopened), "support_event_reopened")
    }

    /// The wireframe prints `DRV-22011-0617`, a composite this platform does not mint (the same
    /// finding C092 recorded about `DRV-22011`), so the row is the **route and the day** — and the day
    /// is read in **Colombo**, because a driver naming yesterday's trip to support must name the same
    /// day support sees.
    func testATripRowIsItsRouteAndItsColomboDay() {
        // 2026-06-11T19:00Z, which is already the 12th in Colombo.
        let trip = tripSummary(startedAt: Date(timeIntervalSince1970: 1_781_204_400))

        let label = SupportLabels.trip(trip)

        XCTAssertTrue(label.contains("Galle Face"), label)
        XCTAssertTrue(label.contains("Nugegoda"), label)
        XCTAssertTrue(label.contains(ScheduleLabels.date(trip.startedAt)), label)
    }
}
