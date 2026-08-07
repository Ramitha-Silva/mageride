import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-006's answer, as a value (US-7.7).
///
/// **What is deliberately absent is the point.** The filter cannot conjure a vehicle the platform
/// did not send: an engaged Mode C vehicle (US-7.16), a stale one (US-7.17) and a Mode B vehicle
/// with no `share:{userId}` grant (D-23) never reach it, because fanout-svc drops all three from the
/// geocell groups. So every assertion here is about what a *passenger's own preference* hides, and
/// nothing here can make one visible.
final class MapFilterTests: XCTestCase {

    private let bus = HomeFixtures.frame(VehicleType.bus, ServiceMode.a)
    private let tuk = HomeFixtures.frame(VehicleType.threeWheeler, ServiceMode.c)
    private let privateVan = HomeFixtures.frame(VehicleType.van, ServiceMode.b)

    func testEverythingIsOnByDefault() {
        // A passenger who has never opened the sheet sees everything the platform sent them, which
        // is what makes the filter a preference rather than a gate.
        let filter = MapFilter()

        XCTAssertTrue(filter.allows(bus), "a bus")
        XCTAssertTrue(filter.allows(tuk), "an on-demand tuk")
        XCTAssertTrue(filter.allows(privateVan), "a Mode B van")
        XCTAssertFalse(filter.showsNothing)
    }

    func testSwitchingAModeOffHidesOnlyThatMode() {
        let noOnDemand = MapFilter().withMode(.c, enabled: false)

        XCTAssertTrue(noOnDemand.allows(bus))
        XCTAssertTrue(noOnDemand.allows(privateVan))
        XCTAssertFalse(noOnDemand.allows(tuk), "the Mode C tuk is filtered out")
    }

    func testSwitchingATypeOffHidesItInEveryMode() {
        let noVans = MapFilter().withType(.van, enabled: false)

        XCTAssertFalse(noVans.allows(privateVan))
        XCTAssertTrue(noVans.allows(bus))
    }

    /// AL-09 has ten types and the wireframe draws eight chips, so `truck` and `mini_truck` have no
    /// control at all. A type with no chip is **unfilterable rather than hidden**: a marker that
    /// vanished with no way to bring it back is a bug a passenger cannot diagnose.
    func testATypeWithNoChipIsNeverHiddenByTheChips() {
        let onlyBuses = MapFilter(modes: Set(VehicleLabels.modeRows), types: [.bus])

        XCTAssertTrue(onlyBuses.allows(HomeFixtures.frame(VehicleType.truck, ServiceMode.a)))
        XCTAssertTrue(onlyBuses.allows(HomeFixtures.frame(VehicleType.miniTruck, ServiceMode.a)))
        XCTAssertFalse(
            onlyBuses.allows(HomeFixtures.frame(VehicleType.sedan, ServiceMode.c)),
            "a sedan has a chip and it is off"
        )
    }

    /// A frame that declares neither a type nor a mode is always drawn — the platform sent it, and
    /// hiding a vehicle for a field it did not fill in would erase it from a map with no control
    /// anywhere to bring it back.
    func testAFrameThatDeclaresNeitherIsAlwaysDrawn() {
        let anonymous = HomeFixtures.frame(nil, nil)

        XCTAssertTrue(MapFilter().allows(anonymous))
        XCTAssertTrue(MapFilter(modes: [], types: []).allows(anonymous))
    }

    func testAnEmptyAxisIsReportedAsShowingNothing() {
        XCTAssertTrue(MapFilter(modes: [], types: Set(VehicleLabels.chipTypes)).showsNothing)
        XCTAssertTrue(MapFilter(modes: Set(VehicleLabels.modeRows), types: []).showsNothing)
        XCTAssertFalse(MapFilter(modes: [.a], types: Set(VehicleLabels.chipTypes)).showsNothing)
    }

    /// The eight chips in the wireframe's own order, and the three that are missing.
    ///
    /// `private` is the Mode B grey — already covered by the toggle one row above — and the two
    /// lorries are delivery-only (AL-09, Epic 20). **Train is its own chip**, which is the whole of
    /// US-7.7's *"trains separate"*.
    func testTheChipsAreTheEightPassengerTypesInTheWireframesOrder() {
        XCTAssertEqual(
            VehicleLabels.chipTypes,
            [.bus, .train, .threeWheeler, .flex, .sedan, .miniVan, .van, .motorbike]
        )
        XCTAssertFalse(VehicleLabels.chipTypes.contains(.truck))
        XCTAssertFalse(VehicleLabels.chipTypes.contains(.miniTruck))
        XCTAssertFalse(VehicleLabels.chipTypes.contains(.privateHire))
        XCTAssertEqual(VehicleLabels.modeRows, [.a, .b, .c])
    }

    /// Every wire enum this app can be handed maps onto a presentation token, and the badge letters
    /// are the wireframe's. A type with no token would be drawn in the fallback grey with the wrong
    /// name — the defect C088 found on the driver side, from the other direction.
    func testEveryWireEnumHasAToken() {
        for token in VehicleToken.allCases where token != .privateHire {
            XCTAssertEqual(VehicleToken.forWire(token.wire), token, "\(token.wire) lost its token")
            XCTAssertFalse(token.nameKey.isEmpty)
        }
        XCTAssertNil(VehicleToken.forType(nil))
        XCTAssertNil(ModeToken.forMode(nil))
        XCTAssertEqual(ModeToken.forMode(ServiceMode.a), .a)
        XCTAssertEqual(ModeToken.forMode(ServiceMode.b), .b)
        XCTAssertEqual(ModeToken.forMode(ServiceMode.c), .c)
        XCTAssertEqual([ModeToken.a.badge, ModeToken.b.badge, ModeToken.c.badge], ["A", "B", "C"])
    }
}

/// SCR-PI-007's two tiles and SCR-PI-010's recent row, as numbers.
///
/// Asserted against the resolved strings rather than against English literals, so the suite says
/// what it means in whichever language a previous test class left the bundle pointing at.
final class MapFormatTests: XCTestCase {

    func testADistanceUnderAKilometreIsMetres() {
        XCTAssertEqual(MapFormat.distance(metres: 350), "popup_distance_m".localisedFormat(350))
        XCTAssertTrue(MapFormat.distance(metres: 350).contains("350"))
    }

    func testADistanceOverAKilometreIsOneDecimalPlace() {
        // The wireframe's own `2.4 km`. Truncated to a tenth rather than rounded, which is what
        // `%1$d.%2$d` over whole metres means and what the Android twin prints.
        XCTAssertEqual(MapFormat.distance(metres: 2_449), "popup_distance_km".localisedFormat(2, 4))
        XCTAssertEqual(MapFormat.distance(metres: 1_000), "popup_distance_km".localisedFormat(1, 0))
    }

    func testADistanceWithNoPassengerPositionSaysSo() {
        XCTAssertEqual(
            MapFormat.distance(from: nil, to: HomeFixtures.colombo),
            "popup_distance_unavailable".localised
        )
    }

    /// Up rather than to-nearest: a bus 89 seconds away is *"2 min"*, and a passenger who reads
    /// *"1 min"* and misses it by twenty seconds was told the wrong thing.
    func testAnEtaIsRoundedUpAndNeverReadsZero() {
        XCTAssertEqual(MapFormat.eta(seconds: 89), "popup_eta_minutes".localisedFormat(2))
        XCTAssertEqual(MapFormat.eta(seconds: 120), "popup_eta_minutes".localisedFormat(2))
        XCTAssertEqual(MapFormat.eta(seconds: 30), "popup_eta_now".localised)
        XCTAssertEqual(MapFormat.eta(seconds: 0), "popup_eta_now".localised)
    }

    /// The lookup has not landed, or it failed. The tile says so rather than inventing a number
    /// from a straight-line distance the platform never promised.
    func testAnAbsentEtaSaysItIsUnavailable() {
        XCTAssertEqual(MapFormat.eta(seconds: nil), "popup_eta_unavailable".localised)
    }
}
