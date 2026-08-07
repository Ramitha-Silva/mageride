import MageRideShared
import XCTest

@testable import DriverApp

/// The fences C092's prompt draws, enforced rather than remembered.
///
/// > *"Parity-fenced to C074. Paired tracker means the phone stops ingesting GPS for that vehicle. Rate
/// > passenger opens as a modal sheet; no caption box on sharing."*
///
/// The first is a fact about ``TrackerPositionPublisher`` and is asserted here. The second and third
/// are facts about **which control a screen draws**, so they are checked the way ``WalletFenceTests``
/// checks AL-05: over the cluster's own source, with comments stripped — half of this component's job
/// is to *document* why the caption box is gone, and a check that fired on the explanation would push
/// the explanation out of the code.
@MainActor
final class TrackerFenceTests: XCTestCase {

    // MARK: - US-3.6 · exactly one publisher

    /// **The gate is a decorator, because there are three doors and not one.** SCR-DI-010's go-online
    /// toggle, SCR-DI-011's Start Journey and US-5.10's Restart all reach the service through
    /// ``PositionPublisher/start(vehicleId:mode:vehicleType:)``; written into any one of them the rule
    /// would be missing from the other two.
    func testAPairedVehicleIsOneThisHandsetNoLongerPublishesFor() async {
        let delegate = FakePositionPublisher()
        let bindings = FakeTrackerBindingStore()
        let publisher = TrackerPositionPublisher(delegate: delegate, bindings: bindings)

        await publisher.start(vehicleId: testVehicleId, mode: ServiceMode.c, vehicleType: VehicleType.sedan)
        XCTAssertEqual(delegate.started, [testVehicleId], "an untracked vehicle publishes normally")

        bindings.remember(TrackerBinding(vehicleId: testVehicleId, imei: "861234567890123", bindingId: "b"))
        await publisher.start(vehicleId: testVehicleId, mode: ServiceMode.c, vehicleType: VehicleType.sedan)

        XCTAssertEqual(delegate.started, [testVehicleId], "the second start was refused")
        XCTAssertTrue(publisher.isTracked(testVehicleId))
    }

    /// A driver with a tracked bus and an untracked tuk goes online on the tuk normally: the vehicle id
    /// is the whole test, and it is the right one — the MQTT username **is** the vehicle id (D6' §3).
    func testTheGateIsPerVehicleAndNotPerHandset() async {
        let delegate = FakePositionPublisher()
        let bindings = FakeTrackerBindingStore()
        bindings.remember(TrackerBinding(vehicleId: testVehicleId, imei: "861234567890123", bindingId: "b"))
        let publisher = TrackerPositionPublisher(delegate: delegate, bindings: bindings)

        await publisher.start(vehicleId: testVehicleId, mode: nil, vehicleType: nil)
        await publisher.start(vehicleId: testOtherVehicleId, mode: nil, vehicleType: nil)

        XCTAssertEqual(delegate.started, [testOtherVehicleId])
    }

    /// **`stop()` is never gated.** Stopping a publisher that is not running is a no-op, and a gate that
    /// could swallow a stop would leave a handset publishing for a vehicle it had just been paired away
    /// from — the exact state the decorator exists to prevent.
    func testStoppingIsNeverRefused() {
        let delegate = FakePositionPublisher()
        let bindings = FakeTrackerBindingStore()
        bindings.remember(TrackerBinding(vehicleId: testVehicleId, imei: "861234567890123", bindingId: "b"))

        TrackerPositionPublisher(delegate: delegate, bindings: bindings).stop()

        XCTAssertEqual(delegate.stopCount, 1)
    }

    // MARK: - AL-35 · the caption box is gone, and the rating is a sheet

    /// The removed box was *"Showing sharing for … temporarily assigned by …"*. What replaced it is the
    /// selector itself, so the screen may name a vehicle and must never name who assigned it.
    func testNoSharingCopyReintroducesTheAssignedByCaptionInAnyOfTheThreeLanguages() {
        for (locale, values) in copy(withPrefix: "sharing_") {
            XCTAssertFalse(values.isEmpty, "no sharing copy in \(locale)")
            let offenders = values.filter { value in
                Self.captionBoxInCopy.contains { value.localizedCaseInsensitiveContains($0) }
            }
            XCTAssertTrue(offenders.isEmpty, "\(locale) brings back AL-35's caption box: \(offenders)")
        }
    }

    /// SCR-DI-030's rating is *"a sheet, detent `.medium`"* and *"no longer an inline card"*, which on
    /// this platform is one modifier — so the fence is checkable as a fact about the source.
    func testTheRatePassengerSheetIsPresentedAndNotInlined() throws {
        let source = try clusterSource(directory: "History", file: "RatePassengerSheet.swift")

        XCTAssertTrue(source.contains("presentationDetents"), "AL-35 — the rating is a presented sheet")
        XCTAssertTrue(source.contains(".medium"), "D2' §SCR-DI-030 fixes the detent")

        let screen = try clusterSource(directory: "History", file: "RideHistoryScreen.swift")
        XCTAssertTrue(screen.contains(".sheet("), "the screen presents it rather than expanding a row")
    }

    /// C092's four destinations are registered and all four hang off the **Menu** tab, which is what
    /// makes the system back button say `‹ Menu` on each of them.
    func testTheFourScreensAreRegisteredUnderTheMenuTab() {
        let routes: [DriverRoute] = [.trackerPairing, .sharing, .profile, .rideHistory]
        let roots = Set(DriverTab.allCases.map(\.route))

        for route in routes {
            XCTAssertTrue(DriverRoute.staticRoutes.contains(route), "\(route.path) is not registered")
            XCTAssertEqual(route.tab, .menu, "\(route.path) hangs off the wrong stack")
            XCTAssertFalse(route.isFullScreenTakeover)
            XCTAssertFalse(route.isPreSession)
            XCTAssertFalse(roots.contains(route), "\(route.path) is pushed, not a tab root")
        }
    }

    /// Every row SCR-DI-036 offers for this component resolves to one of the four.
    func testTheMenuRowsForThisComponentPointAtTheFourScreens() {
        XCTAssertEqual(MenuDestination.trackerPairing.route, .trackerPairing)
        XCTAssertEqual(MenuDestination.sharing.route, .sharing)
        XCTAssertEqual(MenuDestination.profile.route, .profile)
        XCTAssertEqual(MenuDestination.rideHistory.route, .rideHistory)
    }

    // MARK: - Reading the cluster

    /// One Swift file of C092's, with its comments removed.
    ///
    /// Found relative to `#filePath` for ``WalletFenceTests``' reason, and it works for the same one: a
    /// simulator shares the host's filesystem. On a device this fails loudly rather than passing
    /// silently, which is the right way round for a fence.
    private func clusterSource(directory: String, file: String) throws -> String {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()      // DriverAppTests
            .deletingLastPathComponent()      // apps/driver-ios
            .appendingPathComponent("DriverApp/\(directory)/\(file)")

        let text = try String(contentsOf: url, encoding: .utf8)
        var stripped = text.replacingOccurrences(of: #"/\*[\s\S]*?\*/"#, with: " ", options: .regularExpression)
        stripped = stripped.replacingOccurrences(of: #"(?m)//.*$"#, with: " ", options: .regularExpression)
        return stripped
    }

    /// Every value whose key starts with `prefix`, in each of the three `Localizable.strings`.
    private func copy(withPrefix prefix: String) -> [String: [String]] {
        let bundle = Bundle(for: MageRideBundleToken.self)
        var copy: [String: [String]] = [:]
        for locale in ["en", "si", "ta"] {
            guard
                let path = bundle.path(forResource: locale, ofType: "lproj"),
                let localised = Bundle(path: path),
                let url = localised.url(forResource: "Localizable", withExtension: "strings"),
                let table = NSDictionary(contentsOf: url) as? [String: String]
            else {
                XCTFail("cannot read \(locale).lproj/Localizable.strings")
                continue
            }
            copy[locale] = table.filter { $0.key.hasPrefix(prefix) }.map(\.value)
        }
        return copy
    }

    /// The removed caption, in the three languages. *"assigned"* alone is deliberately not on the list:
    /// SCR-DI-026's own group is *"Temporarily assigned to me"* and that one is still true.
    private static let captionBoxInCopy = [
        "showing sharing for",
        "assigned by",
        "පවරා දුන්නේ",
        "ஒதுக்கியவர்",
    ]
}
