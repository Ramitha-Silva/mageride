import XCTest

@testable import DriverApp

/// The build configuration actually reached the bundle.
///
/// Every value here comes from an xcconfig through an `Info.plist` interpolation, and the failure
/// mode of that chain is silent: a misspelled setting name leaves `$(MAGERIDE_…)` in the plist or an
/// empty string in its place, and the first symptom is a request to an empty origin at runtime. On
/// Android the same class of mistake is a compile error, because `BuildConfig` is generated code —
/// this test is what buys the equivalent.
final class DriverEnvironmentTests: XCTestCase {

    private let environment = DriverEnvironment.current

    func testTheGatewayOriginIsAResolvedUrl() {
        XCTAssertFalse(environment.apiBaseUrl.isEmpty, "MAGERIDE_API_BASE_URL did not reach Info.plist")
        XCTAssertFalse(environment.apiBaseUrl.contains("$("), "the xcconfig variable was not interpolated")
        XCTAssertNotNil(URL(string: environment.apiBaseUrl))
        XCTAssertTrue(
            environment.apiBaseUrl.hasPrefix("http://") || environment.apiBaseUrl.hasPrefix("https://"),
            "the `/$()/` escape in the xcconfig collapsed — the value is \(environment.apiBaseUrl)"
        )
    }

    func testTheMqttPlaneIsResolved() {
        XCTAssertFalse(environment.mqttHost.isEmpty, "MAGERIDE_MQTT_HOST did not reach Info.plist")
        XCTAssertTrue([1883, 8883].contains(environment.mqttPort), "1883 plain or 8883 TLS, nothing else")
        XCTAssertEqual(environment.mqttTls, environment.mqttPort == 8883, "TLS and the port must agree")
    }

    func testThePmTilesArchiveIsResolved() {
        XCTAssertFalse(environment.pmTilesUrl.isEmpty, "MAGERIDE_PMTILES_URL did not reach Info.plist")
        XCTAssertTrue(environment.pmTilesUrl.hasSuffix(".pmtiles"), "§0.1: a PMTiles archive, not a tile server")
    }

    /// D-31's input. The gate compares this against the platform's floor, so a build that shipped
    /// `0.0.0` would be permanently behind it.
    func testTheAppVersionIsTheMarketingVersion() {
        XCTAssertNotEqual(environment.appVersion, "0.0.0", "CFBundleShortVersionString is missing")
        XCTAssertTrue(environment.userAgent.contains(environment.appVersion))
        XCTAssertTrue(environment.userAgent.contains("iOS"), "the platform belongs in the User-Agent")
    }

    /// AL-08: the driver and passenger apps must not read each other's session on one handset.
    func testTheKeychainNamespaceIsSurfaceScoped() {
        XCTAssertTrue(environment.keychainService.contains("driver"))
        XCTAssertFalse(environment.keychainService.contains("passenger"))
    }

    /// Debug talks to the compose stack over plaintext; release must never. The check is on the
    /// pair rather than on `mqttTls` alone, because it is the *combination* that is dangerous.
    func testAReleaseBuildIsNeverPointedAtALaptop() {
        guard !environment.isDebug else { return }
        XCTAssertTrue(environment.mqttTls, "a release build must use the TLS listener")
        XCTAssertTrue(environment.apiBaseUrl.hasPrefix("https://"))
        XCTAssertFalse(environment.apiBaseUrl.contains("localhost"))
        XCTAssertFalse(environment.mqttHost.contains("localhost"))
    }
}
