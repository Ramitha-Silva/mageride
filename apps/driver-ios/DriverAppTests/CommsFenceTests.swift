import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// The fences C093's prompt draws, enforced rather than remembered.
///
/// > *"Parity-fenced to C075. VoIP failure offers direct dial; no masked-SMS fallback. CallKit is
/// > used for the VoIP call UI."*
///
/// Some of that is checkable as a fact about types; the rest — *"no masked-SMS fallback appears
/// anywhere"* — is only checkable as a fact about the **source and the copy**, which is the same
/// split ``WalletFenceTests`` makes and for the same reason: a screen is where a withdrawn fallback
/// comes back, because a screen is where somebody adds a button, and a translation nobody on the
/// review reads is where it comes back quietly.
///
/// **The copy is read out of the built bundle and the source off disk**, exactly as
/// ``WalletFenceTests`` does — see that type for why `#filePath` works on a simulator and would
/// (correctly) fail on a device. Comments are stripped first, because half of this cluster's job is
/// to *document* why the masking bridge is gone and a check that fired on the explanation would push
/// the explanation out of the code.
@MainActor
final class CommsFenceTests: XCTestCase {

    // MARK: - AL-48 · one fallback, and it is a dial

    /// **The masked-number PSTN bridge and D-25's masked-SMS relay were both withdrawn.** There is
    /// exactly one fallback from a failed VoIP call and it is a `tel:` dial of the real number the
    /// ride already carries post-accept (US-26.4, BR-30.3).
    ///
    /// Checkable as a fact about the type: ``CallType`` has two cases and neither is a relay, and the
    /// app never sends a third.
    ///
    /// Written out rather than mapped off `CallType.entries` — that property is Kotlin's and does not
    /// cross the bridge as anything a Swift `for` can use, which is the note `WalletLabels` and
    /// `VehicleOnboardingRepository` both carry. `ordinal` is what pins "and there is no third": a
    /// case added to `:shared` shifts one of these two and fails here.
    func testThereAreExactlyTwoCallTypesAndNeitherIsAMaskedRelay() {
        XCTAssertEqual(CallType.freeVoip.name, "FREE_VOIP")
        XCTAssertEqual(CallType.directDial.name, "DIRECT_DIAL")
        XCTAssertEqual([CallType.freeVoip.ordinal, CallType.directDial.ordinal].sorted(), [0, 1])
    }

    /// Every call this cluster starts is one of the two, and the *fallback* is always the dial.
    func testTheOnlyFallbackACallOffersIsADirectDial() throws {
        let offenders = try commsSources().flatMap { file, code in
            Self.withdrawnRelays.filter(code.localizedCaseInsensitiveContains).map { "\(file): \($0)" }
        }

        XCTAssertTrue(
            offenders.isEmpty,
            "AL-48 — the masking bridge and the SMS relay are gone: \(offenders)"
        )
    }

    /// The Sinhala and Tamil files are where a withdrawn fallback is likeliest to survive a review:
    /// nobody reading the English screen would see it.
    ///
    /// **`sos_*` is deliberately exempt.** D-33's alarm genuinely *is* an SMS to an emergency
    /// contact, and its copy has to say so; what AL-48 withdrew is a masked-SMS relay between a
    /// driver and a rider, which is a different thing that no `call_*` string may offer.
    func testNoCallCopyOffersAnSmsInAnyOfTheThreeLanguages() {
        for (locale, values) in clusterCopy(prefix: "call_") {
            XCTAssertFalse(values.isEmpty, "no call copy in \(locale)")
            let offenders = values.filter { value in
                Self.smsInCopy.contains { value.localizedCaseInsensitiveContains($0) }
            }
            XCTAssertTrue(offenders.isEmpty, "\(locale) offers an SMS fallback on a call: \(offenders)")
        }
    }

    /// P-05, as a fact about the wire: the driver calls the **rider**, and `comms.call_log` says so.
    /// The two kind-derived roles exist for the delivery screens and are not what SCR-DI-031 sends.
    func testTheCallRoleIsTheOneP05Fixes() {
        XCTAssertEqual(CalleeRole.passenger.wire, "passenger")
        XCTAssertNotEqual(CalleeRole.passenger, CalleeRole.driver, "the driver never calls themselves")
    }

    // MARK: - CallKit is the call UI

    /// D2' §SCR-DI-031 is *"**iOS** CallKit"* and the wireframe's own cell is titled *"VoIP call
    /// (CallKit)"*. The framework is linked, the provider is real, and the seam the screen talks to
    /// is the one that reports to it.
    func testTheCallScreenIsBackedByARealCallKitProvider() throws {
        let session = try XCTUnwrap(
            commsSources().first { $0.file == "CallKitSession.swift" }?.code,
            "SCR-DI-031's CallKit half is missing"
        )

        for symbol in ["import CallKit", "CXProvider", "CXProviderDelegate", "CXEndCallAction"] {
            XCTAssertTrue(session.contains(symbol), "CallKit is the call UI, and \(symbol) is missing")
        }
    }

    /// **A `.generic` handle, never `.phoneNumber`.** P-05's whole point is that the driver never sees
    /// the rider's number on a free call, and a `.phoneNumber` handle is rendered by the system on the
    /// lock screen *and written into the handset's own call history* — which would leak, to the OS and
    /// to anybody who picked up the phone, precisely the number AL-48 says a VoIP call keeps hidden.
    func testTheReportedCallCarriesNoPhoneNumber() throws {
        let session = try XCTUnwrap(commsSources().first { $0.file == "CallKitSession.swift" }?.code)

        XCTAssertTrue(session.contains("CXHandle(type: .generic"))
        XCTAssertFalse(session.contains(".phoneNumber"), "a reported number is a leaked number")
    }

    /// **The engine this build ships carries no media, and the fence is that nothing pretends
    /// otherwise.** `AbsentVoipEngine` fails with ``VoipFailure/noMediaClient``, the outcome is
    /// reported to voip-svc as `voip_failed`, and AL-48's dial is what the driver is offered — see
    /// that type's own documentation for why the LiveKit package is not here.
    func testTheShippedEngineReportsNoMediaClientRatherThanFakingACall() {
        var links: [CallLink] = []
        AbsentVoipEngine().join(session: testVoipSession) { links.append($0) }

        XCTAssertEqual(links, [.failed(.noMediaClient)])
        XCTAssertEqual(CallOutcome.voipFailed.wire, "voip_failed")
    }

    /// **No `NSMicrophoneUsageDescription`, because no code asks for a microphone.** The mirror of
    /// `apps/driver-android`'s manifest keeping `RECORD_AUDIO` out: a purpose string with nothing
    /// behind it asks a driver for a permission this build cannot use. It lands in the same commit as
    /// the real engine, and this assertion is what will fail then — which is the point.
    func testNoMicrophonePurposeStringShipsWhileNoEngineNeedsOne() {
        let declared = Bundle.main.object(forInfoDictionaryKey: "NSMicrophoneUsageDescription")

        XCTAssertNil(
            declared,
            "a microphone purpose string with no media client behind it asks for a permission this build cannot use"
        )
    }

    // MARK: - D-33 · the alarm has no positionless form

    /// `TriggerSosRequest.lat`/`.lng` are **required**, so there is no request to make without a fix.
    /// BR-29.4 contemplates a positionless SOS for the *web* surface and the app-facing contract
    /// carries no equivalent — the C075 spec gap, carried forward.
    func testTheAlarmCannotBeRaisedWithoutACoordinate() {
        let request = TriggerSosRequest(rideId: testRideId, lat: testHere.lat, lng: testHere.lng, role: SosRole.driver)

        XCTAssertEqual(request.role, SosRole.driver, "this app raises a driver alarm and nothing else")
        XCTAssertEqual(request.lat, testHere.lat)
        XCTAssertEqual(request.lng, testHere.lng)
    }

    /// The alarm is raised from **one** place. A second, unconfirmed door to an irrevocable
    /// `POST /v1/sos` is what SCR-DI-032 exists to prevent: SCR-DI-015's and SCR-DI-016's buttons
    /// navigate here rather than acting.
    func testNothingOutsideTheSafetyClusterRaisesAnAlarm() throws {
        let offenders = try appSources()
            .filter { $0.file != "SosModel.swift" && $0.file != "RideContact.swift" }
            .filter { $0.code.contains("triggerSos") }
            .map(\.file)

        XCTAssertTrue(offenders.isEmpty, "the alarm has one door and it is SCR-DI-032: \(offenders)")
    }

    // MARK: - The four screens

    /// SCR-DI-031 and SCR-DI-032 are **takeovers**: a driver on an alarm screen must not be one tap
    /// from their wallet, and a fifteen-second call is not a destination to swipe back from.
    /// SCR-DI-033 and SCR-DI-034 hang off the **Menu** tab, which is what makes the system back
    /// button say `‹ Menu` on both.
    func testTheTwoTakeoversTakeOverAndTheTwoMenuScreensDoNot() {
        for route in [DriverRoute.voipCall(rideId: testRideId), .sos(rideId: testRideId)] {
            XCTAssertTrue(route.isFullScreenTakeover, "\(route.path) must cover the tab bar")
            XCTAssertFalse(route.isPreSession)
            XCTAssertFalse(
                DriverRoute.staticRoutes.contains(route),
                "\(route.path) is parameterised and cannot be a static route"
            )
        }

        for route in [DriverRoute.support, .notifications] {
            XCTAssertTrue(DriverRoute.staticRoutes.contains(route), "\(route.path) is not registered")
            XCTAssertEqual(route.tab, .menu, "\(route.path) hangs off the wrong stack")
            XCTAssertFalse(route.isFullScreenTakeover)
        }
    }

    /// The two takeovers carry a ride because both are *about* one: `POST /v1/sos` names the trip so
    /// the SMS and the admin live feed say **which**, and `POST /v1/calls/start` takes a `rideId`.
    func testBothTakeoversCarryTheirRide() {
        XCTAssertEqual(DriverRoute.voipCall(rideId: testRideId).path, "call/\(testRideId)")
        XCTAssertEqual(DriverRoute.sos(rideId: testRideId).path, "sos/\(testRideId)")
        XCTAssertNotEqual(
            DriverRoute.sos(rideId: testRideId),
            DriverRoute.sos(rideId: "01JRIDE0000000000000000002"),
            "two rides are two destinations"
        )
    }

    // MARK: - Reading the cluster

    /// Every Swift file under this component's four directories, with its comments removed.
    private func commsSources() throws -> [(file: String, code: String)] {
        try ["Comms", "Safety", "Support", "Notifications"].flatMap(sources(in:))
    }

    /// Every Swift file in the app target, for the one rule that is about the whole app.
    private func appSources() throws -> [(file: String, code: String)] {
        let root = Self.appDirectory
        let manager = FileManager.default
        guard let walker = manager.enumerator(atPath: root.path) else { return [] }

        return try walker.compactMap { entry -> (file: String, code: String)? in
            guard let relative = entry as? String, relative.hasSuffix(".swift") else { return nil }
            let text = try String(contentsOf: root.appendingPathComponent(relative), encoding: .utf8)
            return ((relative as NSString).lastPathComponent, Self.stripComments(text))
        }
    }

    private func sources(in group: String) throws -> [(file: String, code: String)] {
        let directory = Self.appDirectory.appendingPathComponent(group, isDirectory: true)
        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            .filter { $0.hasSuffix(".swift") }
            .sorted()

        XCTAssertFalse(names.isEmpty, "\(group) is not where this test looks: \(directory.path)")

        return try names.map { name in
            let text = try String(contentsOf: directory.appendingPathComponent(name), encoding: .utf8)
            return (name, Self.stripComments(text))
        }
    }

    private static let appDirectory = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()      // DriverAppTests
        .deletingLastPathComponent()      // apps/driver-ios
        .appendingPathComponent("DriverApp", isDirectory: true)

    /// `text` with its block and line comments removed.
    private static func stripComments(_ text: String) -> String {
        var stripped = text.replacingOccurrences(
            of: #"/\*[\s\S]*?\*/"#,
            with: " ",
            options: .regularExpression
        )
        stripped = stripped.replacingOccurrences(of: #"(?m)//.*$"#, with: " ", options: .regularExpression)
        return stripped
    }

    /// Every value in each of the three `Localizable.strings` whose key starts with [prefix].
    private func clusterCopy(prefix: String) -> [String: [String]] {
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

    /// What a withdrawn fallback would be spelled as if it came back — the masked-number bridge
    /// (AL-36 as amended by AL-48) and D-25's masked-SMS relay.
    private static let withdrawnRelays = [
        "maskedNumber",
        "masked_number",
        "MASKED",
        "smsRelay",
        "sms_relay",
        "relayNumber",
        "proxyNumber",
    ]

    /// What an SMS fallback would read as on a call screen, in the three languages.
    private static let smsInCopy = ["SMS", "text message", "කෙටි පණිවිඩ", "குறுஞ்செய்தி"]
}
