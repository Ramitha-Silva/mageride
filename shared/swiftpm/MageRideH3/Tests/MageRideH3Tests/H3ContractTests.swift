import XCTest

@testable import MageRideH3

/// The facts `:shared`'s `domain/geo` package depends on, asserted against the vendored library.
///
/// **These are not tests of H3.** Upstream has its own suite and it is not vendored. What is checked
/// here is the small set of properties MageRide's realtime plane is *built on* — the ones whose
/// silent failure mode is an empty map:
///
/// 1. R-06's view is exactly nineteen cells (`GeoCells.PASSENGER_VIEW_CELL_COUNT`).
/// 2. A view cell is res 7 and its dispatch ancestor is res 5 (`GeoCells.VIEW_RESOLUTION` /
///    `DISPATCH_RESOLUTION`).
/// 3. The canonical spelling round-trips — a cell's lowercase hex is what `cell:{h3index}` carries,
///    and `H3Cell.parse` in `:shared` reads it back.
/// 4. A failure is thrown rather than answered as `0`, because a zero index is a *well-formed
///    looking* group name that nothing publishes to.
///
/// A version bump that changed any of the four would be a platform-wide incident — every passenger
/// subscribing to groups `fanout-svc` does not publish on — so it is turned into a failing build
/// here rather than into a support ticket.
final class H3ContractTests: XCTestCase {

    /// Colombo Fort — `MapCamera.colombo`, and the cold-start camera in both apps.
    private let colomboLat = 6.9344
    private let colomboLng = 79.8428

    /// R-06: *"res-7 + `ring(2)` = 19 cells ≈ 2.8–3.3 km"*.
    func testThePassengerViewIsNineteenCells() throws {
        let origin = try H3.cell(at: colomboLat, colomboLng, resolution: 7)
        let disk = try H3.gridDisk(origin, k: 2)

        XCTAssertEqual(disk.count, 19)
        XCTAssertEqual(Set(disk).count, 19, "the disk must not repeat a cell")
        XCTAssertTrue(disk.contains(origin), "the client's own cell is always in its own view")
    }

    /// The wider intercity view — `GeoView.INTERCITY_5KM`, `1 + 3k(k + 1)` at `k = 3`.
    func testTheIntercityViewIsThirtySevenCells() throws {
        let origin = try H3.cell(at: colomboLat, colomboLng, resolution: 7)
        XCTAssertEqual(try H3.gridDisk(origin, k: 3).count, 37)
    }

    /// `GeoCells.VIEW_RESOLUTION` is 7 and `DISPATCH_RESOLUTION` is 5, and the second is an
    /// *ancestor* of the first — which is what makes `GeoCells.dispatchCell` and the view agree
    /// about where a passenger is.
    func testAViewCellsDispatchAncestorIsResolutionFive() throws {
        let view = try H3.cell(at: colomboLat, colomboLng, resolution: 7)
        let dispatch = try H3.cell(at: colomboLat, colomboLng, resolution: 5)

        XCTAssertEqual(try H3.parent(of: view, resolution: 5), dispatch)
    }

    /// The group name is `cell:{h3index}` in lowercase hex — `LiveHub.cellGroup`, and the same
    /// spelling `H3Cell.token` produces and `H3Cell.parse` reads.
    func testTheCanonicalHexSpellingRoundTrips() throws {
        let cell = try H3.cell(at: colomboLat, colomboLng, resolution: 7)
        let token = String(cell, radix: 16)

        XCTAssertEqual(token, token.lowercased())
        XCTAssertEqual(UInt64(token, radix: 16), cell)
        XCTAssertTrue(H3.isValid(cell))
    }

    /// The centre comes back in **degrees**, near where it went in — the conversion in both
    /// directions, which is the one thing this package adds to the C.
    func testTheCentreComesBackInDegreesNearTheInput() throws {
        let cell = try H3.cell(at: colomboLat, colomboLng, resolution: 7)
        let centre = try H3.centre(of: cell)

        // A res-7 hexagon is ~1.2 km edge to edge, so a centre more than a hundredth of a degree
        // (~1.1 km) away would mean the radians conversion is wrong in one direction.
        XCTAssertEqual(centre.latitude, colomboLat, accuracy: 0.01)
        XCTAssertEqual(centre.longitude, colomboLng, accuracy: 0.01)
    }

    /// A refused call throws. `0` is a *well-formed* index to look at and a group nothing publishes
    /// to — the exact failure `H3Grid`'s KDoc says looks like an empty map with no error anywhere.
    func testAnImpossibleResolutionThrowsRatherThanAnsweringZero() {
        XCTAssertThrowsError(try H3.cell(at: colomboLat, colomboLng, resolution: 42))
    }

    /// Every resolution the platform names resolves, so a future caller of `GeoView` cannot find
    /// one of them missing.
    func testBothResolutionsThePlatformUsesResolve() throws {
        for resolution: Int32 in [5, 7] {
            let cell = try H3.cell(at: colomboLat, colomboLng, resolution: resolution)
            XCTAssertTrue(H3.isValid(cell))
        }
    }
}
