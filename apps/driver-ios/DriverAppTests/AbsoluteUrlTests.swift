import XCTest

@testable import DriverApp

/// Joining the gateway origin to the path registry-svc hands back (Δ MCS-25).
///
/// Slashes are the kind of thing that works against one deployment and breaks against the next —
/// `ApiBaseUrl` carries a trailing one in some configurations and not others — so the rule is
/// written down rather than left to whichever string the simulator happened to be pointed at.
///
/// The Android twin is `AbsoluteUrlTest`, asserting the same five rules.
final class AbsoluteUrlTests: XCTestCase {

    private let signed = "/v1/drivers/01J0/profile-photo?expires=1893456000&signature=abc"

    func testAnOriginWithNoTrailingSlashJoinsCleanly() {
        XCTAssertEqual(
            absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: signed),
            "https://api.mageride.lk\(signed)")
    }

    func testAnOriginWithATrailingSlashDoesNotDoubleIt() {
        XCTAssertEqual(
            absoluteUrl(gatewayOrigin: "https://api.mageride.lk/", path: signed),
            "https://api.mageride.lk\(signed)")
    }

    func testAPathWithNoLeadingSlashIsStillJoinedWithOne() {
        XCTAssertEqual(
            absoluteUrl(gatewayOrigin: "https://api.mageride.lk/", path: "v1/drivers/01J0/profile-photo"),
            "https://api.mageride.lk/v1/drivers/01J0/profile-photo")
    }

    /// D-36 lets `getDriverProfilePhoto` redirect to a presigned bucket URL, and a future read could
    /// carry one directly. Prefixing the gateway origin onto an absolute URL would turn a working
    /// link into a 404 against the wrong host.
    func testAnAbsoluteUrlIsReturnedUntouched() {
        let presigned = "https://objects.mageride.lk/docs/abc?X-Amz-Signature=def"

        XCTAssertEqual(absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: presigned), presigned)
        XCTAssertEqual(
            absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: "http://localhost:9000/x"),
            "http://localhost:9000/x")
    }

    /// A driver with no photograph, which is what PDPA erasure leaves behind and what every driver
    /// reads as until the profile call answers. `nil` is what draws the glyph.
    func testNothingToLoadStaysNil() {
        XCTAssertNil(absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: nil))
        XCTAssertNil(absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: ""))
        XCTAssertNil(absoluteUrl(gatewayOrigin: "https://api.mageride.lk", path: "   "))
    }
}
