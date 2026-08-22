import UIKit
import XCTest

@testable import DriverApp

/// D2' §0.1's map source and §0.2's vehicle legend, on the iOS side of the parity fence.
final class MapAndVehicleTokenTests: XCTestCase {

    private let bundle = Bundle(for: MageRideBundleToken.self)

    // MARK: - The style

    /// §0.1: "MapLibre GL Native … over self-served **PMTiles on Cloudflare R2** — no Google Maps".
    ///
    /// The archive URL is a build setting and the style is a resource, so the resource carries a
    /// placeholder. A style that shipped with the placeholder still in it would load and render
    /// nothing, which is the failure this catches.
    func testTheStyleSubstitutesThePmTilesArchive() throws {
        for dark in [false, true] {
            let json = try MapStyles.json(darkMode: dark, pmTilesUrl: "https://tiles.example/x.pmtiles", bundle: bundle)
            XCTAssertFalse(json.contains("__PMTILES_URL__"), "the placeholder survived substitution")
            XCTAssertTrue(json.contains("pmtiles://https://tiles.example/x.pmtiles"))
            XCTAssertFalse(json.lowercased().contains("google"), "§0.1: no Google Maps")
        }
    }

    /// The two appearances are the same document with a different palette — same source, same
    /// source-layers, same layer ids. A layer present in one and not the other is a map that changes
    /// shape at dusk.
    func testBothAppearancesCarryTheSameLayers() throws {
        let light = try layerIds(darkMode: false)
        let dark = try layerIds(darkMode: true)
        XCTAssertEqual(light, dark)
        XCTAssertEqual(
            light,
            [
                "background", "earth", "landuse", "water", "waterway",
                "buildings", "buildings-outline",
                "roads-minor-casing", "roads-minor", "paths",
                "roads-major-casing", "roads-major",
                "railway", "railway-hatch", "boundaries",
                "water-labels", "road-labels", "road-shields", "railway-labels",
                "pois", "places",
            ],
            "the same twenty-one layers as apps/driver-android/src/main/res/raw/map_style_light.json"
        )
    }

    // MARK: - The layer contract

    /// The ids and feature-property names are the Android ones byte-for-byte. The GeoJSON a screen
    /// feeds these sources is built from the same `:shared` DTOs on both platforms, so a property
    /// renamed on one side is a marker that silently stops being coloured on that side only.
    func testTheLayerAndPropertyNamesMatchAndroid() {
        XCTAssertEqual(VehicleLayers.sourceVehicles, "mageride-vehicles")
        XCTAssertEqual(VehicleLayers.sourceCircles, "mageride-circles")
        XCTAssertEqual(VehicleLayers.layerVehicles, "mageride-vehicles-symbols")
        XCTAssertEqual(VehicleLayers.layerClusters, "mageride-vehicles-clusters")
        XCTAssertEqual(VehicleLayers.layerClusterCount, "mageride-vehicles-cluster-count")
        XCTAssertEqual(VehicleLayers.layerAccuracy, "mageride-accuracy")
        XCTAssertEqual(VehicleLayers.layerGeofence, "mageride-geofence")
        XCTAssertEqual(VehicleLayers.propVehicleType, "vehicleType")
        XCTAssertEqual(VehicleLayers.propHeading, "heading")
        XCTAssertEqual(VehicleLayers.propCircleKind, "circleKind")
    }

    /// AL-12's near-pickup radius and MAP-10's circle are one number.
    func testTheGeofenceRadiusIsAl12s100Metres() {
        XCTAssertEqual(VehicleLayers.geofenceRadiusM, 100)
    }

    // MARK: - The legend

    /// AL-09's canonical set — eleven types, in the order §0.2 prints them.
    ///
    /// **Snake case, because that is what `:shared`'s `VehicleType.wire` is** (Δ C088). The GeoJSON
    /// both driver apps feed `mageride-vehicles` carries the wire enum, so a camel-case table here
    /// silently stops colouring three of the ten types — see ``VehicleToken/wire``.
    func testTheLegendIsTheElevenCanonicalTypes() {
        XCTAssertEqual(
            VehicleToken.allCases.map(\.wire),
            [
                "bus", "train", "motorbike", "three_wheeler", "flex", "sedan",
                "mini_van", "van", "truck", "mini_truck", "private",
            ]
        )
    }

    /// §0.2's SF Symbol column. Every one ships in SF Symbols 4 (iOS 16), which is the deployment
    /// target, so none of them can resolve to nothing at runtime.
    func testEverySymbolIsTheSpecsAndExistsOnTheDeploymentTarget() {
        let expected: [VehicleToken: String] = [
            .bus: "bus.fill",
            .train: "tram.fill",
            .motorbike: "bicycle",
            .threeWheeler: "box.truck.fill",
            .flex: "car.fill",
            .sedan: "car.fill",
            .miniVan: "bus.fill",
            .van: "bus.fill",
            .truck: "box.truck.fill",
            .miniTruck: "box.truck.fill",
            .privateHire: "shippingbox.fill",
        ]
        for token in VehicleToken.allCases {
            XCTAssertEqual(token.symbolName, expected[token], "\(token) has the wrong SF Symbol")
            XCTAssertNotNil(
                UIImage(systemName: token.symbolName),
                "\(token.symbolName) does not exist on this deployment target"
            )
        }
    }

    /// §0.2 calls Train out as a "rail icon, **distinct**": a bus and a train sharing a green
    /// `bus.fill` is the one confusion Mode A cannot afford.
    func testTrainIsVisuallyDistinctFromBus() {
        XCTAssertNotEqual(VehicleToken.train.symbolName, VehicleToken.bus.symbolName)
        XCTAssertNotEqual(VehicleToken.train.wire, VehicleToken.bus.wire)
    }

    /// An unknown wire value is `nil`, not a default. A vehicle type this build does not know drawn
    /// as a sedan is a map that lies.
    func testAnUnknownVehicleTypeHasNoToken() {
        XCTAssertEqual(VehicleToken.forWire("sedan"), .sedan)
        XCTAssertEqual(VehicleToken.forWire("SEDAN"), .sedan, "the lookup is case-insensitive")
        XCTAssertNil(VehicleToken.forWire("Hovercraft"))
    }

    /// The three types whose wire spelling is not one word — the ones the camel-case table used to
    /// answer `nil` for, and which therefore drew grey on the map (Δ C088).
    func testTheMultiWordTypesResolveFromTheirSharedWireSpelling() {
        XCTAssertEqual(VehicleToken.forWire("three_wheeler"), .threeWheeler)
        XCTAssertEqual(VehicleToken.forWire("mini_van"), .miniVan)
        XCTAssertEqual(VehicleToken.forWire("mini_truck"), .miniTruck)
    }

    // MARK: -

    private func layerIds(darkMode: Bool) throws -> [String] {
        let json = try MapStyles.json(darkMode: darkMode, pmTilesUrl: "https://x", bundle: bundle)
        let root = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]
        let layers = root?["layers"] as? [[String: Any]] ?? []
        return layers.compactMap { $0["id"] as? String }
    }
}
