import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-027 · GPS tracker pairing**, and the C092 fence that hangs off it.
///
/// The rules under test are the ones that decide what gets bound and what stops publishing:
/// ``TrackerImei``'s two questions, the selector's scope, `409 imei-duplicate` as a quarantine rather
/// than a retry, and — the one that reaches beyond this screen — *"a paired vehicle is one this handset
/// no longer publishes for"* (US-3.6).
@MainActor
final class TrackerPairingModelTests: XCTestCase {

    private var identity = FakeDriverIdentity()
    private var trackers = FakeTrackerRepository()
    private var publisher = FakePositionPublisher()
    private var camera = FakeCameraAuthoriser()

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        trackers = FakeTrackerRepository()
        publisher = FakePositionPublisher()
        camera = FakeCameraAuthoriser()
        identity.live = LiveVehicle(vehicles: [summary()], live: summary())
    }

    private func makeModel() -> TrackerPairingModel {
        TrackerPairingModel(identity: identity, trackers: trackers, publisher: publisher, camera: camera)
    }

    // MARK: - The IMEI

    /// The contract's pattern is `^\d{15}$` and nothing else — no Luhn check, because neither contract
    /// asks for one and a client that refused a check digit the server accepts makes a good tracker
    /// unpairable at the roadside.
    func testAnImeiIsFifteenDigitsAndSeparatorsAreDroppedRatherThanRejected() {
        XCTAssertEqual(TrackerImei.digits("8612 3456 7890 123"), "861234567890123")
        XCTAssertEqual(TrackerImei.digits("861234-56-789012-3"), "861234567890123")
        XCTAssertTrue(TrackerImei.isValid("8612 3456 7890 123"))
        XCTAssertFalse(TrackerImei.isValid("86123456789012"), "fourteen digits is not an IMEI")
        XCTAssertEqual(TrackerImei.digits("8612345678901234567"), "861234567890123", "capped at fifteen")
        XCTAssertEqual(TrackerImei.grouped("861234567890123"), "8612 3456 7890 123")
    }

    /// No spec says what a vendor prints in the QR, so the payload is searched for the number rather
    /// than parsed for a shape — and a payload with two candidates is refused rather than guessed at.
    func testTheImeiInAQrPayloadIsFoundInAnyShapeAndRefusedWhenAmbiguous() {
        XCTAssertEqual(TrackerImei.imeiIn("861234567890123"), "861234567890123")
        XCTAssertEqual(TrackerImei.imeiIn("IMEI:861234567890123"), "861234567890123")
        XCTAssertEqual(
            TrackerImei.imeiIn("https://prov.example/bind?imei=861234567890123&v=2"),
            "861234567890123"
        )
        XCTAssertEqual(
            TrackerImei.imeiIn("IMEI:861234567890123 (861234567890123)"),
            "861234567890123",
            "the same number twice is one candidate"
        )
        XCTAssertNil(
            TrackerImei.imeiIn("861234567890123 861234567890124"),
            "two different candidates is not an answer"
        )
        XCTAssertNil(
            TrackerImei.imeiIn("89940011223344556677"),
            "a 20-digit ICCID must not yield its first fifteen digits"
        )
    }

    func testAScannedPayloadWithNoImeiLeavesTheFieldAloneAndSaysSo() async {
        let model = makeModel()
        await model.refresh()
        model.onImeiChange("861234567890123")

        model.onScanned("no serial here")

        XCTAssertEqual(model.state.imei, "861234567890123", "a bad scan does not clear a typed IMEI")
        XCTAssertEqual(model.state.errorKey, "tracker_scan_unreadable")
        XCTAssertFalse(model.state.isScanning)
    }

    // MARK: - The selector

    /// The IMEI belongs to the vehicle it was typed for. Carrying it across the selector is how a
    /// tracker gets bound to the wrong one.
    func testChangingTheVehicleClearsTheImeiAndRescopesTheBinding() async {
        identity.live = LiveVehicle(
            vehicles: [summary(), summary(vehicleId: testOtherVehicleId, registrationNumber: "XY-9999")],
            live: summary()
        )
        trackers.store.remember(
            TrackerBinding(vehicleId: testOtherVehicleId, imei: "861234567890123", bindingId: "b")
        )
        let model = makeModel()
        await model.refresh()
        model.onImeiChange("999888777666555")

        model.select(vehicleId: testOtherVehicleId)

        XCTAssertEqual(model.state.imei, "")
        XCTAssertTrue(model.state.isPaired, "the second vehicle's binding is the one now shown")
        XCTAssertFalse(model.state.canPair, "a tracked vehicle cannot be paired again")
    }

    // MARK: - Pairing

    /// **The C092 fence, from the screen's side.** A gate that only refused the *next* start would
    /// leave a live stream running until the driver went offline, so the pair stops the publisher.
    func testPairingBindsTheImeiAndStopsThisPhonePublishing() async {
        let model = makeModel()
        await model.refresh()
        model.onImeiChange("8612 3456 7890 123")
        XCTAssertTrue(model.state.canPair)

        await model.pair()

        XCTAssertEqual(trackers.pairs.map(\.imei), ["861234567890123"], "the digits, not the grouping")
        XCTAssertEqual(trackers.pairs.map(\.vehicleId), [testVehicleId])
        XCTAssertEqual(publisher.stopCount, 1, "US-3.6 — the device is now the single publisher")
        XCTAssertTrue(model.state.isPaired)
        XCTAssertEqual(model.state.imei, "", "the field clears once the bind is accepted")
        XCTAssertNil(model.state.errorKey)
    }

    /// T-08's anti-clone check is not a failure to retry: the serial is already active somewhere, so
    /// the screen says so rather than offering the same button again.
    func testADuplicateImeiIsAQuarantineNoticeAndNotAGenericFailure() async {
        trackers.nextFailure = apiFailure(code: "imei-duplicate")
        let model = makeModel()
        await model.refresh()
        model.onImeiChange("861234567890123")

        await model.pair()

        XCTAssertTrue(model.state.isQuarantined)
        XCTAssertEqual(model.state.errorKey, "tracker_quarantined")
        XCTAssertEqual(publisher.stopCount, 0, "nothing was bound, so nothing stops publishing")
        XCTAssertFalse(model.state.isPaired)
    }

    /// A bind that failed for any other reason is ordinary copy, and the phone keeps publishing.
    func testAFailedPairLeavesThePhonePublishingAndResolvesItsOwnCopy() async {
        trackers.nextFailure = apiFailure(code: "not-owner")
        let model = makeModel()
        await model.refresh()
        model.onImeiChange("861234567890123")

        await model.pair()

        XCTAssertFalse(model.state.isQuarantined)
        XCTAssertEqual(model.state.errorKey, "error_not_owner")
        XCTAssertEqual(publisher.stopCount, 0)
    }

    // MARK: - The scanner

    /// The camera grant is asked for **before** the sheet, because `DataScannerViewController` reports
    /// itself unavailable without it and a sheet that could not scan has nothing to say about why.
    func testTheScannerAsksForTheCameraFirstAndRefusesWhenItCannotRun() async {
        camera.authorisation = .notDetermined
        camera.grantsOnRequest = true
        let model = makeModel()

        await model.startScan()

        XCTAssertEqual(camera.requestCount, 1)
        XCTAssertTrue(model.state.isScanning)

        camera.authorisation = .blocked
        model.cancelScan()
        await model.startScan()

        XCTAssertFalse(model.state.isScanning)
        XCTAssertEqual(model.state.errorKey, "tracker_scan_blocked")
    }

    func testAHandsetWithNoCodeScannerIsToldToTypeTheImei() async {
        camera.isCodeScannerSupported = false
        let model = makeModel()

        XCTAssertFalse(model.isScanSupported, "the Scan button is drawn disabled, not hidden")
        await model.startScan()

        XCTAssertFalse(model.state.isScanning)
        XCTAssertEqual(model.state.errorKey, "tracker_scan_unsupported")
        XCTAssertEqual(camera.requestCount, 0, "a permission is not asked for a scanner that cannot run")
    }

}
