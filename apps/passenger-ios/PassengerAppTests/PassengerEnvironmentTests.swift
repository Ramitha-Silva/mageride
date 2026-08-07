import XCTest

@testable import PassengerApp

/// The xcconfig → `Info.plist` → Swift chain, and the two absences that are rules.
///
/// **This test exists because that chain fails silently.** A misspelled build setting leaves an
/// empty string where the gateway origin should be, and the first symptom is every request failing
/// at runtime; on Android the same mistake is a compile error, because `BuildConfig` is generated.
final class PassengerEnvironmentTests: XCTestCase {

    private var mageRide: [String: Any] {
        Bundle.main.object(forInfoDictionaryKey: PassengerEnvironment.Keys.root) as? [String: Any] ?? [:]
    }

    func testTheGatewayOriginIsInterpolatedAndIsAUrl() {
        let value = PassengerEnvironment.current.apiBaseUrl
        XCTAssertFalse(value.isEmpty, "MAGERIDE_API_BASE_URL did not reach Info.plist")
        XCTAssertFalse(value.contains("$("), "the build setting was not interpolated: \(value)")
        XCTAssertNotNil(URL(string: value), "\(value) is not a URL")
        XCTAssertTrue(
            value.hasPrefix("http://") || value.hasPrefix("https://"),
            "the `/$()/ ` escape in the xcconfig is wrong: \(value)"
        )
    }

    func testThePmTilesUrlIsInterpolatedAndIsAUrl() {
        let value = PassengerEnvironment.current.pmTilesUrl
        XCTAssertFalse(value.isEmpty, "MAGERIDE_PMTILES_URL did not reach Info.plist")
        XCTAssertFalse(value.contains("$("), "the build setting was not interpolated: \(value)")
        XCTAssertNotNil(URL(string: value))
    }

    /// **D3' §3.3's split, asserted as an absence.** Device position *ingest* is MQTT and belongs to
    /// the driver app; passenger realtime-*out* is SignalR over the gateway origin. This app has no
    /// broker host, no foreground publisher and no background-location mode, and
    /// `apps/passenger-android`'s `ManifestTest` asserts the same three things from the other side.
    ///
    /// A key appearing here would mean somebody had started to give this app a second plane.
    func testThereIsNoMqttConfigurationAtAll() {
        for key in ["MqttHost", "MqttPort", "MqttTls"] {
            XCTAssertNil(mageRide[key], "this app has no MQTT plane (D3' §3.3); \(key) must not exist")
        }
    }

    /// The one background mode this app declares. `location` is deliberately absent: nothing here
    /// runs a fix with the screen off, and declaring it would ask App Review for a capability the
    /// app does not use.
    func testTheOnlyBackgroundModeIsRemoteNotification() {
        let modes = Bundle.main.object(forInfoDictionaryKey: "UIBackgroundModes") as? [String] ?? []
        XCTAssertEqual(Set(modes), ["remote-notification"])
    }

    /// SCR-PI-005 asks for when-in-use and nothing more. A purpose string with no API behind it asks
    /// for a permission this app does not want — the fence `apps/driver-ios` kept until C087 needed
    /// the camera.
    func testOnlyTheWhenInUseLocationPurposeStringIsDeclared() {
        XCTAssertNotNil(Bundle.main.object(forInfoDictionaryKey: "NSLocationWhenInUseUsageDescription"))
        for key in [
            "NSLocationAlwaysAndWhenInUseUsageDescription",
            "NSCameraUsageDescription",
            "NSPhotoLibraryUsageDescription",
            "NSMicrophoneUsageDescription",
            "NSContactsUsageDescription",
        ] {
            XCTAssertNil(
                Bundle.main.object(forInfoDictionaryKey: key),
                "\(key) belongs in the commit that opens the API behind it"
            )
        }
    }

    /// The three languages the bundle negotiates against. AL-26's order is SCR-PI-002's, not this
    /// one's — this set is what stops a handset in Hindi resolving to whichever `.lproj` sorted
    /// first.
    func testTheBundleDeclaresExactlyTheThreeLanguages() {
        let declared = Set(Bundle.main.object(forInfoDictionaryKey: "CFBundleLocalizations") as? [String] ?? [])
        XCTAssertEqual(declared, ["si", "ta", "en"])
    }

    /// D-31's input. `X-App-Version` is what the gateway compares against the minimum, so an empty
    /// one is a build that cannot be gated.
    func testTheAppVersionIsRealAndReachesTheUserAgent() {
        let environment = PassengerEnvironment.current
        XCTAssertFalse(environment.appVersion.isEmpty)
        XCTAssertNotEqual(environment.appVersion, "0.0.0", "CFBundleShortVersionString did not resolve")
        XCTAssertTrue(environment.userAgent.hasPrefix("MageRidePassenger/"))
        XCTAssertTrue(environment.userAgent.contains(environment.appVersion))
    }

    /// **AL-08's fence.** The two apps must not read each other's session on a handset that runs
    /// both, and the Keychain service name is the belt — the absence of a shared access group in
    /// `PassengerApp.entitlements` is the braces.
    func testTheKeychainServiceIsNamespacedToThisSurface() {
        let service = PassengerEnvironment.current.keychainService
        XCTAssertEqual(service, "lk.mageride.passenger.keychain")
        XCTAssertFalse(service.contains("driver"))
    }

    /// A debug build talks to a laptop and a release build must not be able to. The Release xcconfig
    /// pins production; this asserts the *shape* rather than the value, because the test runs under
    /// whichever configuration CI chose.
    func testADebugBuildIsTheOnlyOneThatMayTalkToLocalhost() {
        let environment = PassengerEnvironment.current
        if environment.apiBaseUrl.contains("localhost") || environment.apiBaseUrl.contains("127.0.0.1") {
            XCTAssertTrue(environment.isDebug, "a release build is pointed at a laptop")
        } else {
            XCTAssertTrue(environment.apiBaseUrl.hasPrefix("https://"), "production must be TLS")
        }
    }
}
