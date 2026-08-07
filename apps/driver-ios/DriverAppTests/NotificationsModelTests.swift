import XCTest

@testable import DriverApp

/// **SCR-DI-034 · alerts** — the device-local inbox, the kind table and the age labels.
@MainActor
final class NotificationsModelTests: XCTestCase {

    private var inbox = FakeNotificationInbox()

    override func setUp() {
        super.setUp()
        inbox = FakeNotificationInbox()
    }

    private func makeModel() -> NotificationsModel {
        NotificationsModel(inbox: inbox)
    }

    private func settle() async {
        for _ in 0..<8 { await Task.yield() }
    }

    // MARK: - The list

    /// **Read from the device, not from the platform.** There is no *"list my notifications"*
    /// operation on the app-facing surface, so this is `mobile_db_schema.md` §1.6 — which is also why
    /// the screen works with no connection at all.
    func testTheListIsWhateverThisHandsetStored() async {
        inbox.stored = [driverAlert(id: "a"), driverAlert(id: "b")]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.alerts.map(\.id), ["a", "b"])
        XCTAssertFalse(model.state.isLoading)
    }

    /// The empty state is not the loading state: a spinner over an empty column reads as an empty
    /// inbox, which is why D2' asks for a shimmer and why this flag exists.
    func testAnEmptyListIsDistinctFromOneNotYetRead() async {
        let model = makeModel()

        XCTAssertFalse(model.state.isEmpty, "not read yet is not empty")

        await model.refresh()

        XCTAssertTrue(model.state.isEmpty)
    }

    // MARK: - Opening a row

    /// **A row's deep link is resolved, never trusted.** The stored `deeplink` came over the network
    /// inside an APNs payload; an unrecognised one opens nothing rather than being handed to the
    /// navigator.
    func testAKnownDeepLinkResolvesToItsDestination() async {
        inbox.stored = [driverAlert(id: "a", deeplink: "mageride://ride/\(testRideId)")]
        let model = makeModel()
        await model.refresh()

        model.open(model.state.alerts[0])

        XCTAssertEqual(model.state.opening, .activeRide(rideId: testRideId))
    }

    func testAnUnrecognisedDeepLinkOpensNothing() async {
        inbox.stored = [driverAlert(id: "a", deeplink: "https://evil.example/ride/1")]
        let model = makeModel()
        await model.refresh()

        model.open(model.state.alerts[0])

        XCTAssertNil(model.state.opening)
    }

    /// An alert that opens nothing is still marked read: the driver has looked at it, which is the
    /// only thing `read` claims.
    func testAnAlertThatOpensNothingIsStillMarkedRead() async {
        inbox.stored = [driverAlert(id: "a", deeplink: nil)]
        let model = makeModel()
        await model.refresh()

        model.open(model.state.alerts[0])
        await settle()

        XCTAssertTrue(model.state.alerts[0].isRead)
        XCTAssertEqual(inbox.readIds, ["a"])
    }

    /// Marked read **locally and immediately** — the row is the device's own and there is nothing to
    /// confirm, so the weight changes under the driver's finger rather than after a write.
    func testMarkAllReadMovesTheListBeforeTheWrite() async {
        inbox.stored = [driverAlert(id: "a"), driverAlert(id: "b")]
        let model = makeModel()
        await model.refresh()

        model.markAllRead()

        XCTAssertTrue(model.state.alerts.allSatisfy(\.isRead))
        XCTAssertFalse(model.state.hasUnread)
        await settle()
        XCTAssertEqual(inbox.markAllCount, 1)
    }

    /// Nothing to mark is nothing to draw — the toolbar button is absent on a fully-read list.
    func testTheMarkAllActionIsOfferedOnlyWhileSomethingIsUnread() async {
        inbox.stored = [driverAlert(id: "a", isRead: true)]
        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.hasUnread)
    }

    // MARK: - The kind table

    /// **Matched on the wire value, and an unmatched one still draws.** `data.kind` is
    /// notification-svc's catalogue name and grows without a contract change; a driver being shown
    /// nothing is worse than being shown a row with a generic icon.
    func testEveryPushTypeTheWireframeNamesResolvesToItsOwnRow() {
        XCTAssertEqual(AlertKind.of("ride_offer"), .rideOffer)
        XCTAssertEqual(AlertKind.of("RIDE_OFFER"), .rideOffer)
        XCTAssertEqual(AlertKind.of("DIRECTIONAL_EXPIRING"), .directional)
        XCTAssertEqual(AlertKind.of("LOW_BALANCE"), .lowBalance)
        XCTAssertEqual(AlertKind.of("TOPUP_CONFIRMED"), .moneyIn)
        XCTAssertEqual(AlertKind.of("PAYMENT_CONFIRMED"), .moneyIn)
        XCTAssertEqual(AlertKind.of("SHARE_REQUEST"), .share)
        XCTAssertEqual(AlertKind.of("package_picked"), .package)
        XCTAssertEqual(AlertKind.of("package_delivered"), .package)
        XCTAssertEqual(AlertKind.of("SOS_TRIGGERED"), .safety)
        XCTAssertEqual(AlertKind.of("SOS_RESOLVED"), .safety)
    }

    func testAKindThisBuildHasNeverHeardOfStillDraws() {
        let unknown = AlertKind.of("FLEET_ANNOUNCEMENT")

        XCTAssertEqual(unknown, .other)
        XCTAssertEqual(unknown.labelKey, "alert_kind_other")
        XCTAssertFalse(unknown.symbolName.isEmpty)
    }

    /// Every kind carries a label, because a push that arrived with no title falls back to it.
    func testEveryKindHasALabelAndAGlyph() {
        for kind in AlertKind.allCases {
            XCTAssertFalse(kind.labelKey.isEmpty, "\(kind)")
            XCTAssertFalse(kind.symbolName.isEmpty, "\(kind)")
        }
    }

    // MARK: - The age labels

    /// **Elapsed time rather than a calendar comparison.** *"Yesterday"* here means *"about a day
    /// ago"*: a notification is not a business date, so D-38's Colombo rule does not bite — that one
    /// is about `fee_date` and `period_month`, which this is not.
    func testTheAgeLabelsAreTheAndroidTwinsFiveBuckets() {
        let now = Date(timeIntervalSince1970: 1_781_000_000)

        XCTAssertEqual(AlertAge.of(receivedAt: now.addingTimeInterval(-30), now: now).labelKey, "alert_age_now")
        XCTAssertEqual(
            AlertAge.of(receivedAt: now.addingTimeInterval(-120), now: now),
            AlertAge(labelKey: "alert_age_minutes", value: 2)
        )
        XCTAssertEqual(
            AlertAge.of(receivedAt: now.addingTimeInterval(-3_600), now: now),
            AlertAge(labelKey: "alert_age_hours", value: 1)
        )
        XCTAssertEqual(
            AlertAge.of(receivedAt: now.addingTimeInterval(-90_000), now: now).labelKey,
            "alert_age_yesterday"
        )
        XCTAssertEqual(
            AlertAge.of(receivedAt: now.addingTimeInterval(-3 * 86_400), now: now),
            AlertAge(labelKey: "alert_age_days", value: 3)
        )
    }

    /// A clock that has gone backwards — a handset whose time was corrected — reads as "just now"
    /// rather than as a negative age.
    func testAFutureTimestampReadsAsJustNow() {
        let now = Date(timeIntervalSince1970: 1_781_000_000)

        XCTAssertEqual(AlertAge.of(receivedAt: now.addingTimeInterval(60), now: now).labelKey, "alert_age_now")
    }

    /// The two labels that take no argument take none, and the three that do carry the number the
    /// `%1$d` in all three languages expects.
    func testOnlyTheCountingLabelsCarryAnArgument() {
        let now = Date(timeIntervalSince1970: 1_781_000_000)

        XCTAssertNil(AlertAge.of(receivedAt: now, now: now).value)
        XCTAssertNil(AlertAge.of(receivedAt: now.addingTimeInterval(-90_000), now: now).value)
        XCTAssertNotNil(AlertAge.of(receivedAt: now.addingTimeInterval(-120), now: now).value)
    }

    // MARK: - What the delegate files

    /// A push with no id still gets a row: §1.6's own column comment allows a client UUID, and a
    /// notification the driver saw is one they can be shown again.
    func testEveryPushIsFiledIncludingOneWithNoIdentity() async {
        let message = PushMessage(kind: "LOW_BALANCE", deeplink: nil, notificationId: nil, data: [:])

        await inbox.record(message, title: "Low balance", body: "Top up to keep receiving trips")

        XCTAssertEqual(inbox.recorded.count, 1)
        XCTAssertEqual(inbox.recorded.first?.title, "Low balance")
    }
}
