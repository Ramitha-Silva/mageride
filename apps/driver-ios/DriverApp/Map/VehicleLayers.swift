import Foundation
import MapLibre
import UIKit

/// Where the camera should sit when nothing else has said.
struct MapCamera: Equatable {
    let lat: Double
    let lng: Double
    var zoom: Double = MapCamera.defaultZoom

    /// Close enough to read street names, wide enough to see the next junction.
    static let defaultZoom: Double = 15

    /// Colombo Fort. The cold-start camera before the first GNSS fix arrives.
    static let colombo = MapCamera(lat: 6.9344, lng: 79.8428, zoom: 12)
}

/// D2' §0.3's map primitives, installed on a MapLibre style.
///
/// The same seven layers as `apps/driver-android/.../map/VehicleLayers.kt`, with the same ids and
/// the same feature-property names — which matters more than it looks: the GeoJSON a screen feeds
/// these sources is built from the *same* `:shared` DTOs on both platforms, so a property renamed on
/// one side is a marker that silently stops being coloured on that side only.
///
/// | Pattern | What it is here |
/// |---|---|
/// | MAP-03 | `vehicleType` → the ``VehicleToken`` colour, as an `NSExpression` |
/// | MAP-05 | clustering, declared **on the source** |
/// | MAP-06 | `iconRotation` off the feature's own `heading` |
/// | MAP-02 | the accuracy circle |
/// | MAP-10 | the 100 m geofence circle |
///
/// **The shell does not ship a marker image.** The icon is MAP-03's per-type SF Symbol and belongs
/// to the screen that knows which types are on the map — C088 renders one from ``VehicleToken`` and
/// hands it over with `style.setImage(_:forName:)`.
enum VehicleLayers {

    // MARK: - Identifiers. Byte-for-byte the Android ones.

    static let sourceVehicles = "mageride-vehicles"
    static let sourceCircles = "mageride-circles"
    static let layerVehicles = "mageride-vehicles-symbols"
    static let layerClusters = "mageride-vehicles-clusters"
    static let layerClusterCount = "mageride-vehicles-cluster-count"
    static let layerAccuracy = "mageride-accuracy"
    static let layerGeofence = "mageride-geofence"

    /// Feature properties a screen must set. MAP-03 colours on the first, MAP-06 rotates on the
    /// second, and ``circleKind`` selects between the two circle layers.
    static let propVehicleType = "vehicleType"
    static let propHeading = "heading"
    static let propCircleKind = "circleKind"
    static let circleAccuracy = "accuracy"
    static let circleGeofence = "geofence"

    /// AL-12's near-pickup radius, and MAP-10's circle.
    static let geofenceRadiusM = 100

    // MARK: - Installation

    /// Adds the vehicle source and its three layers to [style].
    ///
    /// **Clustering is on the source, not the layer** — that is how MapLibre expresses MAP-05, and
    /// why the cluster layers filter on `cluster == YES` while the symbol layer filters on its
    /// absence. Without the paired predicates a clustered point renders twice: once as its cluster
    /// and once as itself.
    ///
    /// - Parameters:
    ///   - markerImage: the image name already set on the style. See this type's note.
    ///   - clusterColour: the `primaryContainer` of the theme in force.
    ///   - clusterTextColour: its `onPrimaryContainer`.
    static func addVehicles(
        to style: MLNStyle,
        markerImage: String,
        clusterColour: UIColor,
        clusterTextColour: UIColor
    ) {
        let source = MLNShapeSource(
            identifier: sourceVehicles,
            shape: nil,
            options: [
                .clustered: true,
                // Below zoom 14 a city's worth of taxis is an unreadable smear of pins; above it a
                // driver is looking at individual vehicles near a junction.
                .maximumZoomLevelForClustering: clusterMaxZoom,
                .clusterRadius: clusterRadiusPx,
            ]
        )
        style.addSource(source)

        let vehicles = MLNSymbolStyleLayer(identifier: layerVehicles, source: source)
        vehicles.iconImageName = NSExpression(forConstantValue: markerImage)
        // MAP-06. Rotation off the feature's own heading, aligned to the map so the arrow points
        // where the vehicle is going rather than where the screen is turned.
        vehicles.iconRotation = NSExpression(forKeyPath: propHeading)
        vehicles.iconRotationAlignment = NSExpression(forConstantValue: "map")
        vehicles.iconAllowsOverlap = NSExpression(forConstantValue: true)
        vehicles.iconIgnoresPlacement = NSExpression(forConstantValue: true)
        vehicles.predicate = NSPredicate(format: "cluster != YES")
        style.addLayer(vehicles)

        let clusters = MLNCircleStyleLayer(identifier: layerClusters, source: source)
        clusters.circleColor = NSExpression(forConstantValue: clusterColour)
        // The typed initialiser rather than an `NSExpression(format:)` string: MapLibre's expression
        // function names are a compatibility surface inherited from the SDK it forked, and a
        // mistyped one is a runtime exception on the first style load rather than a compile error.
        clusters.circleRadius = NSExpression(
            forMLNStepping: NSExpression(forKeyPath: "point_count"),
            from: NSExpression(forConstantValue: clusterRadiusSmall),
            stops: NSExpression(forConstantValue: [
                clusterStepMedium: clusterRadiusMedium,
                clusterStepLarge: clusterRadiusLarge,
            ])
        )
        clusters.predicate = NSPredicate(format: "cluster == YES")
        style.addLayer(clusters)

        let counts = MLNSymbolStyleLayer(identifier: layerClusterCount, source: source)
        counts.text = NSExpression(format: "CAST(point_count, 'NSString')")
        counts.textFontSize = NSExpression(forConstantValue: clusterTextSize)
        counts.textColor = NSExpression(forConstantValue: clusterTextColour)
        counts.textIgnoresPlacement = NSExpression(forConstantValue: true)
        counts.textAllowsOverlap = NSExpression(forConstantValue: true)
        counts.predicate = NSPredicate(format: "cluster == YES")
        style.addLayer(counts)
    }

    /// Adds MAP-02's accuracy circle and MAP-10's 100 m geofence circle.
    ///
    /// One source and two layers rather than two sources: both are circles around a point and differ
    /// only in radius and paint, and a feature's ``propCircleKind`` selects between them. Both go
    /// **below** the vehicle symbols, or a driver's own marker sits under its own accuracy halo.
    ///
    /// The radii are a screen measurement in MapLibre and a metre is not, so the caller passes
    /// pixels computed from the current zoom — the same contract as the Android side.
    static func addCircles(to style: MLNStyle, accuracyColour: UIColor, geofenceColour: UIColor) {
        let source = MLNShapeSource(identifier: sourceCircles, shape: nil, options: nil)
        style.addSource(source)

        let accuracy = MLNCircleStyleLayer(identifier: layerAccuracy, source: source)
        accuracy.circleColor = NSExpression(forConstantValue: accuracyColour)
        accuracy.circleOpacity = NSExpression(forConstantValue: accuracyOpacity)
        accuracy.circleStrokeWidth = NSExpression(forConstantValue: 0)
        accuracy.predicate = NSPredicate(format: "%K == %@", propCircleKind, circleAccuracy)

        let geofence = MLNCircleStyleLayer(identifier: layerGeofence, source: source)
        geofence.circleColor = NSExpression(forConstantValue: geofenceColour)
        geofence.circleOpacity = NSExpression(forConstantValue: geofenceOpacity)
        geofence.circleStrokeColor = NSExpression(forConstantValue: geofenceColour)
        geofence.circleStrokeWidth = NSExpression(forConstantValue: geofenceStrokePx)
        geofence.predicate = NSPredicate(format: "%K == %@", propCircleKind, circleGeofence)

        if let anchor = style.layer(withIdentifier: layerVehicles) {
            style.insertLayer(accuracy, below: anchor)
            style.insertLayer(geofence, below: anchor)
        } else {
            style.addLayer(accuracy)
            style.addLayer(geofence)
        }
    }

    /// MAP-03's legend as an `NSExpression` over ``propVehicleType``.
    ///
    /// Built from ``VehicleToken`` rather than from constants here, so the one place a vehicle colour
    /// is written stays the asset catalogue. The fallback is `vehPrivate` grey — a vehicle type this
    /// build does not know is still drawn, because a missing marker reads as a platform outage and
    /// an unfamiliar grey one reads as what it is.
    static func markerColourExpression() -> NSExpression {
        var stops: [NSExpression: NSExpression] = [:]
        for token in VehicleToken.allCases {
            // Lower-cased, matching `VehicleType.name.lowercase()` on the Android side — the GeoJSON
            // both apps build carries the wire enum, and the two have to agree on its case.
            stops[NSExpression(forConstantValue: token.wire.lowercased())] =
                NSExpression(forConstantValue: UIColor(token.color))
        }
        return NSExpression(
            forMLNMatchingKey: NSExpression(forKeyPath: propVehicleType),
            in: stops,
            default: NSExpression(forConstantValue: UIColor(VehicleToken.privateHire.color))
        )
    }

    private static let clusterMaxZoom = 14
    private static let clusterRadiusPx = 60
    private static let clusterRadiusSmall: CGFloat = 16
    private static let clusterRadiusMedium: CGFloat = 22
    private static let clusterRadiusLarge: CGFloat = 28
    private static let clusterStepMedium = 10
    private static let clusterStepLarge = 40
    private static let clusterTextSize: CGFloat = 12
    private static let accuracyOpacity: CGFloat = 0.15
    private static let geofenceOpacity: CGFloat = 0.08
    private static let geofenceStrokePx: CGFloat = 2
}
