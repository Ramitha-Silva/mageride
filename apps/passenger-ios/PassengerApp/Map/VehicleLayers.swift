import Foundation
import MapLibre
import UIKit

/// The MAP-* primitives D2' §0.3 makes a hard rule, installed onto a MapLibre style.
///
/// > *"MapLibre GL Native style (light/dark PMTiles), `SymbolLayer` markers by `vehVeh*` color +
/// > heading arrow (MAP-06), `ClusterLayer` when zoomed out (MAP-05), interpolated marker animation
/// > (MAP-04), `LineLayer` trip polyline (MAP-08), accuracy circle (MAP-02), 100m geofence circle
/// > (MAP-10). Pins: `pickup` green, `dropoff` red, `user` blue dot."*
///
/// Everything on that list except MAP-04 is a layer, and every one of them is here; MAP-04 is
/// ``MarkerInterpolator``, because a tween is arithmetic rather than cartography and MapLibre does
/// not tween a source's contents. MAP-09 is the one item this component does not carry — see
/// ``MapStyles``.
///
/// **This owns the sources and the layers, not their contents**: what goes *in* a source is
/// ``MageRideMap``'s and, above it, the screen's.
///
/// **The identifiers and the feature-property names are byte-for-byte the Android ones**, which
/// matters more than it looks: the features both apps build come from the *same* `:shared` DTOs, so
/// a property renamed on one side is a marker that silently stops being coloured on that side only.
///
/// **Colours are passed in, never written here.** They come from ``MapPalette``; a `UIColor(red:…)`
/// in this file would be the second copy of §0.2's table.
enum VehicleLayers {

    // MARK: - Identifiers. Byte-for-byte `apps/passenger-android/.../map/VehicleLayers.kt`'s.

    /// Source id for live vehicle positions. Features carry ``propVehicleType`` and ``propHeading``.
    static let sourceVehicles = "mageride-vehicles"

    /// Source id for MAP-02's accuracy circle and MAP-10's 100 m geofence.
    static let sourceCircles = "mageride-circles"

    /// Source id for MAP-08's trip polyline.
    static let sourceRoute = "mageride-route"

    /// Source id for SCR-PI-009's walk-to-halt line.
    static let sourceWalk = "mageride-walk"

    /// Source id for §0.3's pickup / dropoff / user pins.
    static let sourcePins = "mageride-pins"

    static let layerVehicles = "mageride-vehicles-symbols"
    static let layerClusters = "mageride-vehicles-clusters"
    static let layerClusterCount = "mageride-vehicles-cluster-count"
    static let layerAccuracy = "mageride-accuracy"
    static let layerGeofence = "mageride-geofence"
    static let layerRouteCasing = "mageride-route-casing"
    static let layerRoute = "mageride-route-line"
    static let layerWalk = "mageride-walk-line"
    static let layerPins = "mageride-pins-circles"

    /// Feature property carrying the canonical vehicle type (AL-09), so MAP-03 can colour it.
    static let propVehicleType = "vehicleType"

    /// Feature property carrying the heading in degrees — MAP-06's arrow rotation.
    static let propHeading = "heading"

    /// Feature property carrying the vehicle id, so MAP-07's tap knows what was tapped.
    static let propVehicleId = "vehicleId"

    /// Feature property distinguishing the accuracy circle from the geofence one.
    static let propCircleKind = "circleKind"

    /// Feature property selecting a pin's colour.
    static let propPinKind = "pinKind"

    static let circleAccuracy = "accuracy"
    static let circleGeofence = "geofence"

    static let pinPickup = "pickup"
    static let pinDropoff = "dropoff"
    static let pinUser = "user"

    /// D5' §7's arrival geofence, and the radius MAP-10 draws.
    static let geofenceRadiusMetres: Double = 100

    /// The style image id the vehicle symbol layer draws — MAP-06's heading arrow.
    static let markerImage = "mageride-vehicle"

    // MARK: - Installation

    /// Installs every layer, **in draw order**, on a style that has just loaded.
    ///
    /// The order is the argument for having one function rather than five: MapLibre stacks a layer
    /// on top of whatever is already there, so circles first (they are context, and a vehicle drawn
    /// under its own accuracy halo disappears), then the trip polyline and the walking leg, then the
    /// vehicles, then the pins that mark where the passenger is going. A caller that added them
    /// itself would get the order right the first time and wrong the second.
    ///
    /// - Parameter markerImage: the image already set on the style with `style.setImage(_:forName:)`.
    ///   ``MageRideMap`` renders it from ``VehicleToken``; nothing here ships an asset.
    static func install(on style: MLNStyle, markerImage: String, palette: MapPalette) {
        addCircles(to: style, palette: palette.circles)
        addRoute(to: style, palette: palette.route)
        addWalk(to: style, colour: palette.route.walk)
        addVehicles(to: style, markerImage: markerImage, palette: palette.vehicles)
        addPins(to: style, palette: palette.pins)
    }

    /// MAP-03 / MAP-05 / MAP-06 — the vehicle source and its three layers.
    ///
    /// **Clustering is on the source, not the layer** — that is how MapLibre expresses MAP-05, and
    /// why the cluster layers filter on `cluster == YES` while the symbol layer filters on its
    /// absence. Without the paired predicates a clustered point renders twice: once as its cluster
    /// and once as itself.
    private static func addVehicles(to style: MLNStyle, markerImage: String, palette: VehiclePalette) {
        let source = MLNShapeSource(
            identifier: sourceVehicles,
            shape: nil,
            options: [
                .clustered: true,
                // Below zoom 14 a city's worth of vehicles is an unreadable smear of pins; above it
                // a passenger is looking at individual vehicles near a junction.
                .maximumZoomLevelForClustering: clusterMaxZoom,
                .clusterRadius: clusterRadiusPx,
            ]
        )
        style.addSource(source)

        let vehicles = MLNSymbolStyleLayer(identifier: layerVehicles, source: source)
        vehicles.iconImageName = NSExpression(forConstantValue: markerImage)
        // MAP-03. The whole eleven-colour legend as one `match` — this map draws every mode in a
        // cell at once, so a single tint (which is what the driver's own-vehicle map uses) cannot
        // express it.
        vehicles.iconColor = markerColourExpression(palette: palette)
        // MAP-06. Rotation off the feature's own heading, aligned to the map so the arrow points
        // where the vehicle is going rather than where the screen is turned.
        vehicles.iconRotation = NSExpression(forKeyPath: propHeading)
        vehicles.iconRotationAlignment = NSExpression(forConstantValue: "map")
        vehicles.iconAllowsOverlap = NSExpression(forConstantValue: true)
        vehicles.iconIgnoresPlacement = NSExpression(forConstantValue: true)
        vehicles.predicate = NSPredicate(format: "cluster != YES")
        style.addLayer(vehicles)

        let clusters = MLNCircleStyleLayer(identifier: layerClusters, source: source)
        clusters.circleColor = NSExpression(forConstantValue: palette.cluster)
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
        counts.textColor = NSExpression(forConstantValue: palette.onCluster)
        counts.textIgnoresPlacement = NSExpression(forConstantValue: true)
        counts.textAllowsOverlap = NSExpression(forConstantValue: true)
        counts.predicate = NSPredicate(format: "cluster == YES")
        style.addLayer(counts)
    }

    /// MAP-02's accuracy circle and MAP-10's 100 m geofence.
    ///
    /// One source and two layers rather than two sources: both are circles around a point and differ
    /// only in radius and paint, and a feature's ``propCircleKind`` selects between them.
    ///
    /// **The radii are metres and MapLibre's `circleRadius` is pixels**, so both are re-scaled
    /// whenever the camera settles — see ``MageRideMap``. A radius set once is wrong at every other
    /// zoom, which is the whole point of a *100 m* geofence.
    private static func addCircles(to style: MLNStyle, palette: CirclePalette) {
        let source = MLNShapeSource(identifier: sourceCircles, shape: nil, options: nil)
        style.addSource(source)

        let accuracy = MLNCircleStyleLayer(identifier: layerAccuracy, source: source)
        accuracy.circleColor = NSExpression(forConstantValue: palette.accuracy)
        accuracy.circleOpacity = NSExpression(forConstantValue: accuracyOpacity)
        accuracy.circleStrokeWidth = NSExpression(forConstantValue: 0)
        accuracy.predicate = NSPredicate(format: "%K == %@", propCircleKind, circleAccuracy)
        style.addLayer(accuracy)

        let geofence = MLNCircleStyleLayer(identifier: layerGeofence, source: source)
        geofence.circleColor = NSExpression(forConstantValue: palette.geofence)
        geofence.circleOpacity = NSExpression(forConstantValue: geofenceOpacity)
        geofence.circleStrokeColor = NSExpression(forConstantValue: palette.geofence)
        geofence.circleStrokeWidth = NSExpression(forConstantValue: geofenceStrokePx)
        geofence.predicate = NSPredicate(format: "%K == %@", propCircleKind, circleGeofence)
        style.addLayer(geofence)
    }

    /// MAP-08 — the trip polyline, as two line layers.
    ///
    /// A casing under the line rather than one stroke: the basemap's own roads are white on grey in
    /// the light style, so a single orange line at four pixels reads as a road on a screen in
    /// sunlight. The casing is the same geometry, wider and darker, which is how every navigation
    /// map draws a route and is the cheapest way to keep it legible over both styles.
    private static func addRoute(to style: MLNStyle, palette: RoutePalette) {
        let source = MLNShapeSource(identifier: sourceRoute, shape: nil, options: nil)
        style.addSource(source)

        let casing = MLNLineStyleLayer(identifier: layerRouteCasing, source: source)
        casing.lineColor = NSExpression(forConstantValue: palette.casing)
        casing.lineWidth = NSExpression(forConstantValue: routeCasingWidthPx)
        casing.lineCap = NSExpression(forConstantValue: "round")
        casing.lineJoin = NSExpression(forConstantValue: "round")
        style.addLayer(casing)

        let line = MLNLineStyleLayer(identifier: layerRoute, source: source)
        line.lineColor = NSExpression(forConstantValue: palette.line)
        line.lineWidth = NSExpression(forConstantValue: routeWidthPx)
        line.lineCap = NSExpression(forConstantValue: "round")
        line.lineJoin = NSExpression(forConstantValue: "round")
        style.addLayer(line)
    }

    /// SCR-PI-009's *"blue walking polyline … to the closest halt"*.
    ///
    /// **Blue and dashed, and both matter.** The route line is the vehicle's colour and solid; this
    /// one is the passenger's own leg of the journey and is not a road the bus takes. D2'
    /// §SCR-PA-009 spells the treatment out — *"`LineLayer` **blue, dashed**"* — and drawing it in
    /// the same style as the route would say the bus goes to your front door.
    private static func addWalk(to style: MLNStyle, colour: UIColor) {
        let source = MLNShapeSource(identifier: sourceWalk, shape: nil, options: nil)
        style.addSource(source)

        let walk = MLNLineStyleLayer(identifier: layerWalk, source: source)
        walk.lineColor = NSExpression(forConstantValue: colour)
        walk.lineWidth = NSExpression(forConstantValue: walkWidthPx)
        walk.lineCap = NSExpression(forConstantValue: "round")
        // Dash lengths are in line widths, not points, so the pattern scales with the stroke and
        // stays legible at every zoom.
        walk.lineDashPattern = NSExpression(forConstantValue: [walkDash, walkGap])
        style.addLayer(walk)
    }

    /// §0.3's *"`pickup` green, `dropoff` red, `user` blue dot"*.
    ///
    /// Circles rather than teardrop symbols, because a circle needs no asset and the spec's own word
    /// for the third one is "dot". A screen that wants a labelled teardrop for a pickup adds its own
    /// symbol layer above this one; what must not vary between screens is the *colour*, which is why
    /// the three are one table passed in.
    private static func addPins(to style: MLNStyle, palette: PinPalette) {
        let source = MLNShapeSource(identifier: sourcePins, shape: nil, options: nil)
        style.addSource(source)

        var stops: [NSExpression: NSExpression] = [:]
        for (kind, colour) in palette.byKind {
            stops[NSExpression(forConstantValue: kind)] = NSExpression(forConstantValue: colour)
        }

        let pins = MLNCircleStyleLayer(identifier: layerPins, source: source)
        pins.circleColor = NSExpression(
            forMLNMatchingKey: NSExpression(forKeyPath: propPinKind),
            in: stops,
            // The user dot's own colour is the fallback, because an unknown pin kind is a client bug
            // and drawing nothing would hide it.
            default: NSExpression(forConstantValue: palette.byKind[pinUser] ?? palette.stroke)
        )
        pins.circleRadius = NSExpression(forConstantValue: pinRadiusPx)
        pins.circleStrokeColor = NSExpression(forConstantValue: palette.stroke)
        pins.circleStrokeWidth = NSExpression(forConstantValue: pinStrokePx)
        style.addLayer(pins)
    }

    /// MAP-03's legend as an `NSExpression` over ``propVehicleType``.
    ///
    /// The fallback is `vehPrivate` grey — a vehicle type this build does not know is still drawn,
    /// because a missing marker reads as a platform outage and an unfamiliar grey one reads as what
    /// it is.
    static func markerColourExpression(palette: VehiclePalette) -> NSExpression {
        var stops: [NSExpression: NSExpression] = [:]
        for (wire, colour) in palette.legend {
            stops[NSExpression(forConstantValue: wire)] = NSExpression(forConstantValue: colour)
        }
        return NSExpression(
            forMLNMatchingKey: NSExpression(forKeyPath: propVehicleType),
            in: stops,
            default: NSExpression(forConstantValue: palette.fallback)
        )
    }

    static let clusterMaxZoom = 14
    static let clusterRadiusPx = 60
    private static let clusterRadiusSmall: CGFloat = 16
    private static let clusterRadiusMedium: CGFloat = 22
    private static let clusterRadiusLarge: CGFloat = 28
    private static let clusterStepMedium = 10
    private static let clusterStepLarge = 40
    private static let clusterTextSize: CGFloat = 12
    private static let accuracyOpacity: CGFloat = 0.15
    private static let geofenceOpacity: CGFloat = 0.08
    private static let geofenceStrokePx: CGFloat = 2
    private static let routeWidthPx: CGFloat = 5
    private static let routeCasingWidthPx: CGFloat = 9

    /// Narrower than the route: a walking leg is a hint, not the journey.
    private static let walkWidthPx: CGFloat = 4
    private static let walkDash: CGFloat = 2
    private static let walkGap: CGFloat = 2
    private static let pinRadiusPx: CGFloat = 7
    private static let pinStrokePx: CGFloat = 2

    /// How faded a last-known marker is (SCR-PI-032). Legible, and unmistakably not live.
    static let staleOpacity: CGFloat = 0.45
}
