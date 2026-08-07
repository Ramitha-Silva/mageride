import MageRideH3
import MageRideShared
import XCTest

@testable import PassengerApp

/// The binding C017 and C085 left to this component, checked against `:shared`'s own rules.
///
/// `MageRideH3`'s package tests check the vendored library; this checks the **adapter** — that a
/// Kotlin `H3Grid` call arrives at the right C function with the right units, and that the two
/// resolutions `domain/geo` names round-trip through it. The failure this exists to catch is silent:
/// a grid that answers plausible-looking ids nothing publishes to.
final class H3GridTests: XCTestCase {

    private let grid = SharedH3Grid()
    private let colombo = GeoPoint(lat: 6.9344, lng: 79.8428)

    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
    }

    /// R-06 through the adapter, at the resolution and ring `GeoCells` fixes — not at numbers typed
    /// out here, which is what makes this a test of the *binding* rather than of arithmetic.
    func testTheViewIsNineteenCellsAtTheResolutionGeoCellsFixes() {
        let origin = grid.cellAt(point: colombo, resolution: Int32(GeoCells.shared.VIEW_RESOLUTION))
        let disk = grid.gridDisk(origin: origin, k: Int32(GeoView.passenger3km.ring))

        XCTAssertEqual(disk.count, Int(GeoCells.shared.PASSENGER_VIEW_CELL_COUNT))
        XCTAssertEqual(disk.count, 19)
        XCTAssertTrue(disk.contains(origin))
    }

    /// `GeoCells.viewCells` is the shared entry point every caller should use, and it goes through
    /// this adapter. If the two ever disagreed, one of them would be a second grid.
    func testTheSharedHelperAndTheAdapterAgree() {
        let viaHelper = GeoCells.shared.viewCells(grid: grid, centre: colombo, view: .passenger3km)
        let origin = grid.cellAt(point: colombo, resolution: Int32(GeoCells.shared.VIEW_RESOLUTION))
        let viaAdapter = grid.gridDisk(origin: origin, k: 2)

        XCTAssertEqual(viaHelper, viaAdapter)
    }

    /// Every cell the client would join is structurally valid and at res 7 — `H3Cell.isWellFormed`
    /// is `:shared`'s own bit-layout check, so this is the two sides of the bridge agreeing about
    /// what a cell index *is*.
    func testEveryCellIsWellFormedAndAtTheViewResolution() {
        let cells = GeoCells.shared.viewCells(grid: grid, centre: colombo, view: .passenger3km)
        for cell in cells {
            XCTAssertTrue(cell.isWellFormed, "\(cell.token) is not a well-formed cell index")
            XCTAssertEqual(cell.resolution, GeoCells.shared.VIEW_RESOLUTION)
            XCTAssertEqual(H3Cell.companion.parseOrNull(token: cell.token), cell, "the token does not round-trip")
        }
    }

    /// The res-5 dispatch index is an *ancestor* of the res-7 view cell, which is what makes
    /// `GeoCells.dispatchCell` and the view agree about where a passenger is.
    func testTheDispatchCellIsTheViewCellsAncestor() {
        let view = grid.cellAt(point: colombo, resolution: Int32(GeoCells.shared.VIEW_RESOLUTION))
        let dispatch = GeoCells.shared.dispatchCell(grid: grid, point: colombo)

        XCTAssertEqual(dispatch.resolution, GeoCells.shared.DISPATCH_RESOLUTION)
        XCTAssertEqual(grid.parent(cell: view, resolution: Int32(GeoCells.shared.DISPATCH_RESOLUTION)), dispatch)
    }

    /// **Degrees in, degrees out.** H3's C API is radians and every MageRide coordinate is degrees,
    /// so a missing conversion in either direction is a cell on the other side of the planet — which
    /// would still be a well-formed index.
    func testTheCentreComesBackInDegreesNearTheInput() {
        let cell = grid.cellAt(point: colombo, resolution: 7)
        let centre = grid.center(cell: cell)

        // A res-7 hexagon is ~1.2 km across; a hundredth of a degree is ~1.1 km.
        XCTAssertEqual(centre.lat, colombo.lat, accuracy: 0.01)
        XCTAssertEqual(centre.lng, colombo.lng, accuracy: 0.01)
    }

    /// Sri Lanka, corner to corner: Point Pedro to Dondra Head, and the two coasts. Every one is a
    /// real coordinate the platform will be asked about, and each must give a nineteen-cell view.
    func testTheWholeOperatingAreaResolves() {
        let places: [(String, Double, Double)] = [
            ("Colombo Fort", 6.9344, 79.8428),
            ("Point Pedro", 9.8167, 80.2333),
            ("Dondra Head", 5.9236, 80.5906),
            ("Trincomalee", 8.5874, 81.2152),
            ("Kandy", 7.2906, 80.6337),
        ]
        for (name, lat, lng) in places {
            let cells = GeoCells.shared.viewCells(grid: grid, centre: GeoPoint(lat: lat, lng: lng), view: .passenger3km)
            XCTAssertEqual(cells.count, 19, "\(name)")
        }
        XCTAssertEqual(SharedH3Grid.failures, 0, "the grid refused a real coordinate")
    }

    /// **A refusal is a value, not a throw.** The Kotlin interface has no failure channel and an
    /// Objective-C exception raised into Kotlin would terminate the process, so a bad input answers
    /// index `0` — which is not well-formed, whose disk is empty, and whose net effect is therefore
    /// *no groups joined* rather than a wrong subscription.
    func testAnImpossibleResolutionRefusesRatherThanThrowing() {
        let cell = grid.cellAt(point: colombo, resolution: 42)

        XCTAssertEqual(cell.index, 0)
        XCTAssertFalse(cell.isWellFormed)
        XCTAssertTrue(grid.gridDisk(origin: cell, k: 2).isEmpty)
        XCTAssertGreaterThan(SharedH3Grid.failures, 0, "a refusal must be counted")
    }

    /// The library agrees the cells are resolvable — stronger than `isWellFormed`, which is a bit
    /// layout check and stops short of the deleted subsequence of a pentagon.
    func testTheLibraryItselfValidatesEveryCellTheClientWouldJoin() {
        let cells = GeoCells.shared.viewCells(grid: grid, centre: colombo, view: .passenger3km)
        for cell in cells {
            XCTAssertTrue(H3.isValid(UInt64(bitPattern: cell.index)), "\(cell.token)")
        }
    }

    /// Thread-safe and side-effect free, which `H3Grid`'s own KDoc requires: SCR-PI-010 calls this on
    /// every position fix.
    func testTheSameCoordinateAlwaysGivesTheSameCell() {
        let first = grid.cellAt(point: colombo, resolution: 7)
        for _ in 0..<100 {
            XCTAssertEqual(grid.cellAt(point: colombo, resolution: 7), first)
        }
    }
}
