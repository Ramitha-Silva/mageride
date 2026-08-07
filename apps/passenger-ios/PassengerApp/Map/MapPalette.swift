import SwiftUI
import UIKit

/// Where the camera sits.
///
/// A value rather than a binding: ``MageRideMap`` moves the camera when this changes and the
/// passenger panning does not write back. A screen that wants to follow something sets it on every
/// fix; a screen that opens somewhere and then leaves the passenger alone sets it once.
struct MapCamera: Equatable {
    var lat: Double
    var lng: Double
    var zoom: Double = MapCamera.defaultZoom

    /// Close enough to read street names, wide enough to see the next junction.
    static let defaultZoom: Double = 15

    /// Colombo Fort. The cold-start camera before the first fix arrives — the same coordinate the
    /// driver app opens on, so two apps side by side on a desk are looking at the same place.
    static let colombo = MapCamera(lat: 6.9344, lng: 79.8428, zoom: 12)
}

/// Every colour the §0.3 layer stack needs, resolved from D2' §0.2 once.
///
/// **Passed into ``VehicleLayers`` rather than read there**, for the reason the Android twin gives:
/// the layer code stays free of SwiftUI and free of hexes, and the one place a vehicle colour is
/// written stays the asset catalogue. `UIColor` because that is what MapLibre's `NSExpression`s take.
///
/// Resolved against a `UITraitCollection` rather than the environment's `ColorScheme`: the style is
/// re-installed on every appearance change (see ``MageRideMap``), and a `Color` converted with
/// `UIColor(_:)` alone resolves against whatever trait happens to be current on the thread — which
/// inside a `UIViewRepresentable`'s coordinator is not reliably the view's.
struct MapPalette {

    let vehicles: VehiclePalette
    let circles: CirclePalette
    let route: RoutePalette
    let pins: PinPalette

    static func resolved(darkMode: Bool) -> MapPalette {
        let traits = UITraitCollection(userInterfaceStyle: darkMode ? .dark : .light)

        func colour(_ value: Color) -> UIColor {
            UIColor(value).resolvedColor(with: traits)
        }

        var legend: [String: UIColor] = [:]
        for token in VehicleToken.allCases {
            // `VehicleToken.wire` **is** `:shared`'s `VehicleType.wire`, which is what the GeoJSON
            // this app builds carries. Nothing here re-cases it: a case fix applied at the read
            // would be a second spelling of the same value.
            legend[token.wire] = colour(token.color)
        }

        return MapPalette(
            vehicles: VehiclePalette(
                legend: legend,
                fallback: colour(VehicleToken.privateHire.color),
                cluster: colour(MageRideColor.primaryContainer),
                onCluster: colour(MageRideColor.onPrimaryContainer)
            ),
            circles: CirclePalette(
                accuracy: colour(MageRideColor.secondary),
                geofence: colour(MageRideColor.primary)
            ),
            route: RoutePalette(
                line: colour(MageRideColor.primary),
                casing: colour(MageRideColor.onSurface),
                walk: colour(MageRideColor.secondary)
            ),
            pins: PinPalette(
                byKind: [
                    VehicleLayers.pinPickup: colour(MageRideColor.pinPickup),
                    VehicleLayers.pinDropoff: colour(MageRideColor.pinDropoff),
                    VehicleLayers.pinUser: colour(MageRideColor.pinUser),
                ],
                stroke: colour(MageRideColor.background)
            )
        )
    }
}

/// MAP-03's eleven-colour legend, plus MAP-05's cluster bubble.
struct VehiclePalette {
    /// Wire name → colour. All eleven of §0.2's rows.
    let legend: [String: UIColor]
    /// `vehPrivate` grey — what an unknown type is drawn in.
    let fallback: UIColor
    let cluster: UIColor
    let onCluster: UIColor
}

/// MAP-02's accuracy halo and MAP-10's 100 m geofence.
struct CirclePalette {
    let accuracy: UIColor
    let geofence: UIColor
}

/// MAP-08's trip line and SCR-PI-009's walk-to-halt leg.
struct RoutePalette {
    let line: UIColor
    let casing: UIColor
    /// *"blue, dashed"* — D2' §SCR-PA-009's own treatment for the walking leg.
    let walk: UIColor
}

/// §0.3's *"`pickup` green, `dropoff` red, `user` blue dot"*.
struct PinPalette {
    let byKind: [String: UIColor]
    /// The ring around a pin, so it reads on both a pale road and a dark one.
    let stroke: UIColor
}
