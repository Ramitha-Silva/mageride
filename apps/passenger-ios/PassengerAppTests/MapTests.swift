import MageRideShared
import XCTest

@testable import PassengerApp

/// MAP-03's legend, MAP-04's tween, MAP-08's polyline decode, and the layer ids both platforms share.
///
/// Everything here is arithmetic or a table — the rendering path itself needs a GL surface and is
/// where `xcodebuild` on a Mac earns its keep.
final class MapTests: XCTestCase {

    // MARK: - MAP-03

    /// **`VehicleToken.wire` IS `:shared`'s `VehicleType.wire`**, and the two being one string is
    /// what makes MAP-03 work: the marker colour is a MapLibre `match` over a GeoJSON attribute
    /// carrying the wire value, so a camel-cased token matches nothing and the vehicle is drawn in
    /// the fallback grey. That defect shipped on the driver side until C088 caught it, and three of
    /// the ten types were affected — every snake-cased one.
    func testEveryVehicleTokenSpellsItsWireValueAsSharedDoes() {
        // Typed out from `Enums.kt`'s `VehicleType`, which is `registry.vehicles.vehicle_type`'s own
        // CHECK domain — the same discipline `ThemeTokenTests` follows for §0.2. Reading the Kotlin
        // enum back and comparing it with itself would prove the compiler works.
        let sharedWireValues = [
            "bus", "train", "motorbike", "three_wheeler", "flex",
            "sedan", "mini_van", "van", "truck", "mini_truck",
        ]
        for wire in sharedWireValues {
            let token = VehicleToken.forWire(wire)
            XCTAssertNotNil(token, "no token for \(wire) — MAP-03 would draw it grey")
            XCTAssertEqual(token?.wire, wire)
        }
        XCTAssertEqual(
            Set(VehicleToken.allCases.map(\.wire)),
            Set(sharedWireValues + [VehicleToken.privateHire.wire]),
            "the presentation table and the wire enum have drifted"
        )
    }

    /// §0.2's eleventh legend row. `private` is the fallback colour and has no `VehicleType`
    /// counterpart — it is a *presentation* row, not a type the platform stores.
    func testTheLegendHasElevenRowsAndPrivateIsNotAVehicleType() {
        XCTAssertEqual(VehicleToken.allCases.count, 11, "§0.2's legend has eleven rows")
        // `private` is a presentation row, not a type the platform stores: `VehicleType` has ten.
        XCTAssertEqual(VehicleToken.allCases.filter { $0 != .privateHire }.count, 10)
    }

    /// The three snake-cased ones, spelled out, because they are the ones that broke.
    func testTheSnakeCasedTokensAreSnakeCased() {
        XCTAssertEqual(VehicleToken.threeWheeler.wire, "three_wheeler")
        XCTAssertEqual(VehicleToken.miniVan.wire, "mini_van")
        XCTAssertEqual(VehicleToken.miniTruck.wire, "mini_truck")
    }

    /// §0.2's *"rail icon, distinct"* — a bus and a train sharing a green `bus.fill` is the one
    /// confusion Mode A cannot afford, and on this surface Mode A is most of what a passenger looks
    /// at.
    func testTheTrainHasItsOwnSymbol() {
        XCTAssertNotEqual(VehicleToken.train.symbolName, VehicleToken.bus.symbolName)
        XCTAssertEqual(VehicleToken.train.symbolName, "tram.fill")
        XCTAssertEqual(VehicleToken.bus.symbolName, "bus.fill")
    }

    /// An unknown wire value answers `nil` rather than a default: an unknown vehicle type drawn as a
    /// sedan is a map that lies, and a server that adds a type should show up as a grey marker
    /// rather than a wrong one.
    func testAnUnknownWireValueHasNoToken() {
        XCTAssertNil(VehicleToken.forWire("hovercraft"))
    }

    /// The layer and property names are byte-for-byte `apps/passenger-android`'s. The features both
    /// apps build come from the same `:shared` DTOs, so a property renamed on one side is a marker
    /// that silently stops being coloured on that side only.
    func testTheLayerAndPropertyNamesAreTheAndroidOnes() {
        XCTAssertEqual(VehicleLayers.sourceVehicles, "mageride-vehicles")
        XCTAssertEqual(VehicleLayers.sourceCircles, "mageride-circles")
        XCTAssertEqual(VehicleLayers.sourceRoute, "mageride-route")
        XCTAssertEqual(VehicleLayers.sourceWalk, "mageride-walk")
        XCTAssertEqual(VehicleLayers.sourcePins, "mageride-pins")
        XCTAssertEqual(VehicleLayers.layerVehicles, "mageride-vehicles-symbols")
        XCTAssertEqual(VehicleLayers.layerClusters, "mageride-vehicles-clusters")
        XCTAssertEqual(VehicleLayers.layerClusterCount, "mageride-vehicles-cluster-count")
        XCTAssertEqual(VehicleLayers.layerAccuracy, "mageride-accuracy")
        XCTAssertEqual(VehicleLayers.layerGeofence, "mageride-geofence")
        XCTAssertEqual(VehicleLayers.propVehicleType, "vehicleType")
        XCTAssertEqual(VehicleLayers.propHeading, "heading")
        XCTAssertEqual(VehicleLayers.propVehicleId, "vehicleId")
        XCTAssertEqual(VehicleLayers.propCircleKind, "circleKind")
        XCTAssertEqual(VehicleLayers.propPinKind, "pinKind")
    }

    /// MAP-10 and D5' §7's arrival geofence are the same hundred metres.
    func testTheGeofenceRadiusIsAHundredMetres() {
        XCTAssertEqual(VehicleLayers.geofenceRadiusMetres, 100)
    }

    /// §0.3's three pin kinds and nothing else.
    func testThePinKindsAreTheSpecsThree() {
        XCTAssertEqual(
            Set([VehicleLayers.pinPickup, VehicleLayers.pinDropoff, VehicleLayers.pinUser]),
            ["pickup", "dropoff", "user"]
        )
    }

    /// A frame becomes a marker with the **wire** type, because that is what the `match` expression
    /// is built over. A frame with no heading points north rather than not being drawn.
    func testAFrameBecomesAMarkerWithItsWireTypeAndADefaultHeading() {
        let frame = VehicleFrame(
            vehicleId: "V1", lat: 6.9, lng: 79.8, heading: nil, speed: nil, type: .threeWheeler, mode: nil
        )
        let marker = frame.asMapVehicle

        XCTAssertEqual(marker.type, "three_wheeler")
        XCTAssertEqual(marker.heading, 0)
        XCTAssertEqual(marker.vehicleId, "V1")
    }

    // MARK: - MAP-04

    /// A vehicle seen for the first time appears **at** its position rather than gliding in from
    /// nowhere.
    func testANewVehicleAppearsAtItsPosition() {
        let interpolator = MarkerInterpolator()
        interpolator.onFrames([marker("V1", lat: 6.9, lng: 79.8)], now: 0)

        XCTAssertEqual(interpolator.markers(at: 0).first?.lat, 6.9)
        XCTAssertTrue(interpolator.isSettled(at: 0))
    }

    /// **The glide lasts as long as the last gap did, per vehicle.** A bus reporting every two
    /// seconds glides for two; a tuk reporting every eight glides for eight. A fixed duration would
    /// make one of the two sprint-and-freeze.
    func testTheGlideLastsAsLongAsTheGapAndLandsOnTheTarget() {
        let interpolator = MarkerInterpolator()
        interpolator.onFrames([marker("V1", lat: 0, lng: 0)], now: 0)
        interpolator.onFrames([marker("V1", lat: 2, lng: 0)], now: 2)

        XCTAssertEqual(interpolator.markers(at: 2).first?.lat ?? -1, 0, accuracy: 0.0001, "starts where it was")
        XCTAssertEqual(interpolator.markers(at: 3).first?.lat ?? -1, 1, accuracy: 0.0001, "half way at half time")
        XCTAssertEqual(interpolator.markers(at: 4).first?.lat ?? -1, 2, accuracy: 0.0001, "lands on the target")
        XCTAssertTrue(interpolator.isSettled(at: 4))
    }

    /// A burst of two batches inside one second must not animate over a few milliseconds — that is
    /// the jump MAP-04 exists to remove — and a vehicle that went quiet for a minute must not glide
    /// smoothly along a path nothing travelled.
    func testTheGlideIsClampedAtBothEnds() {
        XCTAssertEqual(MarkerInterpolator.defaultMinimumDuration, 1)
        XCTAssertEqual(MarkerInterpolator.defaultMaximumDuration, 8)

        let interpolator = MarkerInterpolator()
        interpolator.onFrames([marker("V1", lat: 0, lng: 0)], now: 0)
        interpolator.onFrames([marker("V1", lat: 1, lng: 0)], now: 0.01)
        XCTAssertFalse(interpolator.isSettled(at: 0.5), "a 10 ms gap must still glide for the minimum")
        XCTAssertTrue(interpolator.isSettled(at: 1.02))
    }

    /// Removal is immediate and never animated: a vehicle that went on hire, went stale or whose
    /// share was revoked must leave the map now.
    func testAVehicleAbsentFromABatchIsDroppedImmediately() {
        let interpolator = MarkerInterpolator()
        interpolator.onFrames([marker("V1", lat: 0, lng: 0), marker("V2", lat: 1, lng: 1)], now: 0)
        interpolator.onFrames([marker("V1", lat: 0, lng: 0)], now: 1)

        XCTAssertEqual(interpolator.markers(at: 1).map(\.vehicleId), ["V1"])
    }

    /// **A vehicle turning from 350° to 10° has turned twenty degrees right**, not three hundred and
    /// forty left — the naive linear form spins MAP-06's arrow through a whole rotation every time a
    /// vehicle crosses due north.
    func testABearingInterpolatesTheShortWayRound() {
        XCTAssertEqual(MarkerInterpolator.interpolateBearing(from: 350, to: 10, t: 0.5), 0, accuracy: 0.0001)
        XCTAssertEqual(MarkerInterpolator.interpolateBearing(from: 10, to: 350, t: 0.5), 0, accuracy: 0.0001)
        XCTAssertEqual(MarkerInterpolator.interpolateBearing(from: 0, to: 90, t: 0.5), 45, accuracy: 0.0001)
        XCTAssertEqual(MarkerInterpolator.interpolateBearing(from: 90, to: 90, t: 0.5), 90, accuracy: 0.0001)
    }

    /// Markers come back in the order the vehicles were first seen — a map whose markers reshuffled
    /// on every batch would restart the tween from the wrong track.
    func testMarkersKeepTheirFirstSeenOrder() {
        let interpolator = MarkerInterpolator()
        interpolator.onFrames([marker("V1", lat: 0, lng: 0), marker("V2", lat: 1, lng: 1)], now: 0)
        interpolator.onFrames([marker("V2", lat: 1, lng: 1), marker("V1", lat: 0, lng: 0)], now: 1)

        XCTAssertEqual(interpolator.markers(at: 1).map(\.vehicleId), ["V1", "V2"])
    }

    // MARK: - MAP-08

    /// The algorithm's own worked example, from Google's specification: `(38.5, -120.2)`,
    /// `(40.7, -120.95)`, `(43.252, -126.453)`.
    func testTheCanonicalPolylineDecodes() {
        let points = EncodedPolyline.decode("_p~iF~ps|U_ulLnnqC_mqNvxq`@")

        XCTAssertEqual(points.count, 3)
        XCTAssertEqual(points[0].lat, 38.5, accuracy: 0.00001)
        XCTAssertEqual(points[0].lng, -120.2, accuracy: 0.00001)
        XCTAssertEqual(points[1].lat, 40.7, accuracy: 0.00001)
        XCTAssertEqual(points[1].lng, -120.95, accuracy: 0.00001)
        XCTAssertEqual(points[2].lat, 43.252, accuracy: 0.00001)
        XCTAssertEqual(points[2].lng, -126.453, accuracy: 0.00001)
    }

    /// **A truncated shape draws the part it understood.** A route line is decoration on a booking
    /// screen, not the booking, and half a delta applied to the running latitude would put a point
    /// somewhere arbitrary — which is more misleading than a line that simply stops.
    func testAMalformedShapeYieldsAShortListRatherThanAnError() {
        XCTAssertTrue(EncodedPolyline.decode(nil).isEmpty)
        XCTAssertTrue(EncodedPolyline.decode("").isEmpty)
        XCTAssertEqual(EncodedPolyline.decode("_p~iF~ps|U_ulLnnqC_mqN").count, 2, "the incomplete pair is dropped")
    }

    // MARK: -

    private func marker(_ id: String, lat: Double, lng: Double, heading: Double = 0) -> MapVehicle {
        MapVehicle(vehicleId: id, lat: lat, lng: lng, heading: heading, type: "sedan")
    }
}
