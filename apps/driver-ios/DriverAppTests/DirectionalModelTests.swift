import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-013 · Directional Travel** (US-6A.17–23, DT-01..DT-08).
///
/// The rule this screen exists to make visible is DT-03/US-6A.19: **a use is spent on activation and
/// turning the filter off does not give it back**. Everything else here is the honest limit of what a
/// client can evaluate — the ceiling it has not been told, and the presence it cannot read.
@MainActor
final class DirectionalModelTests: XCTestCase {

    private var standby: FakeStandbyRepository!
    private var location: FakeDriverLocationSource!

    override func setUp() {
        super.setUp()
        standby = FakeStandbyRepository()
        location = FakeDriverLocationSource()
    }

    private func makeModel() -> DirectionalModel {
        DirectionalModel(standby: standby, location: location)
    }

    private var nugegoda: DirectionalDestination {
        DirectionalDestination(label: "Nugegoda", point: testThere)
    }

    // MARK: - Set

    func testSetIsDeadUntilADestinationIsChosen() async {
        standby.filter = directionalFilter(usesRemaining: 2)
        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.canSet)
        model.choose(nugegoda)
        XCTAssertTrue(model.state.canSet)
        XCTAssertEqual(model.state.query, "Nugegoda", "choosing fills the field")
    }

    func testSetIsDeadOnceTheDaysUsesAreSpent() async {
        standby.filter = directionalFilter(usesRemaining: 0)
        let model = makeModel()
        await model.refresh()
        model.choose(nugegoda)

        XCTAssertFalse(model.state.canSet, "the wireframe's 'uses exhausted → Set disabled'")
    }

    func testSetIsDeadWhileAFilterIsAlreadyRunning() async {
        standby.filter = directionalFilter(active: true, usesRemaining: 1, secondsLeft: 3_600)
        let model = makeModel()
        await model.refresh()
        model.choose(nugegoda)

        XCTAssertFalse(model.state.canSet)
    }

    /// The label sent is the driver's own shorthand, capped at the sixty characters the request allows.
    func testSettingSendsTheDestinationAndACappedLabel() async {
        let long = String(repeating: "a", count: 90)
        let model = makeModel()
        await model.refresh()
        model.choose(DirectionalDestination(label: long, point: testThere))

        await model.setDirection()

        XCTAssertEqual(standby.setDirectionals.count, 1)
        XCTAssertEqual(standby.setDirectionals.first?.destination.lat, testThere.lat)
        XCTAssertEqual(standby.setDirectionals.first?.label?.count, 60)
        XCTAssertTrue(model.state.isActive)
        XCTAssertEqual(model.state.maxDurationSec, 7_200, "learned from the activation, not baked in")
    }

    /// **`403 not-online` is the server's answer to a control this screen cannot gate** — there is no
    /// presence read on `dispatch.yaml`, so the driver taps and reads copy.
    func testBeingOfflineBecomesCopyRatherThanADisabledButton() async {
        standby.nextFailure = apiFailure(code: "not-online", status: 403)
        let model = makeModel()
        await model.refresh()
        model.choose(nugegoda)
        XCTAssertTrue(model.state.canSet, "the client cannot know presence, so it does not guess")

        await model.setDirection()

        XCTAssertEqual(model.state.errorKey, "error_not_online")
        XCTAssertFalse(model.state.isActive)
    }

    func testTheDailyLimitBecomesItsOwnCopy() async {
        standby.nextFailure = apiFailure(code: "directional-limit-reached", status: 409)
        let model = makeModel()
        await model.refresh()
        model.choose(nugegoda)

        await model.setDirection()

        XCTAssertEqual(model.state.errorKey, "error_directional_limit")
    }

    // MARK: - Turn off

    /// **US-6A.19** — the activation is spent either way. Without the rule a driver could flick the
    /// filter on for the one offer they wanted and off again, all day, on two uses.
    func testTurningOffKeepsTheUseSpent() async {
        standby.filter = directionalFilter(active: true, usesRemaining: 1, secondsLeft: 3_600, label: "Nugegoda")
        standby.cleared = DirectionalFilterCleared(active: false, usesRemaining: 1)
        let model = makeModel()
        await model.refresh()
        XCTAssertTrue(model.state.isActive)

        await model.turnOff()

        XCTAssertEqual(standby.clearedCount, 1)
        XCTAssertFalse(model.state.isActive)
        XCTAssertEqual(model.state.usesRemaining, 1, "the same count the server sent back")
        XCTAssertNil(model.state.destination)
        XCTAssertEqual(model.state.query, "")
    }

    // MARK: - The card

    func testTheCountdownRunsFromTheDeadlineAndFloorsAtZero() async {
        // Half a minute past the hour-and-42 boundary, so the assertion is about the *shape* of the
        // countdown rather than about how many milliseconds the test took to reach it.
        standby.filter = directionalFilter(active: true, secondsLeft: 6_150, label: "Nugegoda")
        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(MoneyFormat.countdown(seconds: model.state.timeRemainingSeconds), "1:42")

        standby.filter = directionalFilter(active: true, secondsLeft: -60, label: "Nugegoda")
        await model.refresh()
        XCTAssertEqual(model.state.timeRemainingSeconds, 0)
    }

    /// DT-08 / US-10.14's ten-minute warning is the same threshold notify-svc's `directional.expiring`
    /// push uses.
    func testTheTenMinuteWarningMatchesTheServersOwnThreshold() async {
        XCTAssertEqual(DirectionalState.preExpiryReminderSeconds, 600)

        standby.filter = directionalFilter(active: true, secondsLeft: 500, label: "Nugegoda")
        let model = makeModel()
        await model.refresh()
        XCTAssertTrue(model.state.isExpiringSoon)

        standby.filter = directionalFilter(active: true, secondsLeft: 900, label: "Nugegoda")
        await model.refresh()
        XCTAssertFalse(model.state.isExpiringSoon)
    }

    /// MAP-06's ➤ points at the destination, not at where the vehicle happens to be facing.
    func testTheHeadingIsTheBearingToTheDestination() async {
        let model = makeModel()
        model.start()
        location.emit(testFix(testHere))
        model.choose(nugegoda)

        // `testThere` is due north of `testHere`.
        XCTAssertEqual(model.state.headingDeg ?? -1, 0, accuracy: 0.5)
    }

    func testWithNoFixThereIsNoHeadingRatherThanAGuess() {
        let model = makeModel()
        model.choose(nugegoda)
        XCTAssertNil(model.state.headingDeg)
    }

    // MARK: - The destination field

    func testTheSavedShortcutsComeFirstAndSurviveASearch() async {
        standby.shortcuts = [
            SavedAddress(
                addressId: "01JADDRESS0000000000000001",
                label: "Home",
                line1: "Nawala",
                line2: nil,
                line3: nil,
                lat: testThere.lat,
                lng: testThere.lng,
                isHome: KotlinBoolean(value: true),
                isWork: nil
            ),
        ]
        standby.places = [
            GeocodedPlace(
                lat: testThere.lat,
                lng: testThere.lng,
                displayName: "Nugegoda",
                line1: nil,
                city: nil,
                source: nil
            ),
        ]

        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.suggestions.map(\.label), ["Home"])
        XCTAssertTrue(model.state.suggestions.first?.isHome == true)

        model.typed("Nug")
        try? await Task.sleep(nanoseconds: 500_000_000)

        XCTAssertEqual(model.state.suggestions.map(\.label), ["Home", "Nugegoda"])
        XCTAssertEqual(standby.searches, ["Nug"])
    }

    /// Two characters is the whole country; the geocoder is not asked.
    func testAShortQueryIsNotSearched() async {
        let model = makeModel()
        model.typed("Nu")
        try? await Task.sleep(nanoseconds: 500_000_000)

        XCTAssertTrue(standby.searches.isEmpty)
        XCTAssertEqual(model.state.query, "Nu", "the field still shows what was typed")
    }
}
