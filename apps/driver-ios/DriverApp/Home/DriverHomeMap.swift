import MageRideShared
import MapLibre
import SwiftUI
import UIKit

/// **AL-31 — the driver home map shows the driver's OWN active vehicle and nothing else.**
///
/// D2' §SCR-DI-010 is explicit: *"other drivers' active vehicles are never shown on the driver home
/// map"*. It is not a filter applied to a wider feed — there is no wider feed to filter.
/// `LiveMapScope.DriverHomeMap` joins **no geocell group at all**, by construction, which is also why
/// `iosAppModule` binds no `H3Grid`: nothing on this surface resolves one. A future component that
/// wanted demand heat here would be adding a subscription, not relaxing a filter, and would need a
/// micro-change-set first.
///
/// The marker is MAP-06's heading arrow tinted by MAP-03's per-type colour, drawn through the shell's
/// own ``VehicleLayers`` so this vehicle is the same colour here, on My Vehicles and on the passenger
/// map. MAP-02's accuracy circle sits under it.
///
/// - Parameters:
///   - position: The handset's last fix. `nil` before the first one — the map opens on Colombo Fort
///     and moves as soon as GNSS answers, rather than showing an empty grey rectangle.
///   - token: The live vehicle's legend token, for the marker colour; `nil` falls back to the grey
///     `vehPrivate`, which is what an unknown type is meant to look like.
///   - heading: Overrides the fix's own bearing for MAP-06's arrow. SCR-DI-013 points it at the
///     Directional destination rather than at where the vehicle happens to be facing — that is the
///     *"➤ heading marker"* the wireframe draws on the filter's map preview.
///   - geofence: MAP-10's 100 m circle, at the point the driver is being sent to. SCR-DI-015's pickup
///     pin; `nil` everywhere else.
struct DriverHomeMap: View {

    let position: Fix?
    var token: VehicleToken?
    var heading: Double?
    var geofence: GeoPoint?

    @EnvironmentObject private var graph: DriverGraph
    @State private var style: MLNStyle?
    @State private var mapView: MLNMapView?

    var body: some View {
        ZStack(alignment: .topTrailing) {
            MageRideMapView(
                camera: position.map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? MapCamera.colombo,
                pmTilesUrl: graph.environment.pmTilesUrl,
                onStyleLoaded: install
            )

            MapRecentreButton(action: recentre)
                .padding(MageRideSpacing.sm)
        }
        .onChange(of: DriverHomeMap.featureKey(position, token, heading, geofence)) { _ in
            draw()
        }
    }

    /// Installs the layers and the one marker image this map needs.
    ///
    /// Called on **every** style load, not just the first: switching between light and dark re-loads
    /// the style and drops every layer added to it, which is what ``MageRideMapView/onStyleLoaded``
    /// exists for.
    private func install(_ style: MLNStyle, _ map: MLNMapView) {
        // The shell installs no marker asset on purpose — the icon belongs to the screen that knows
        // which types are on the map. Here that is one, and MAP-06 wants an arrow rather than §0.2's
        // per-type silhouette: the symbol is rotated to the vehicle's bearing, and a rotated bus
        // points nowhere. It is the same shape `ic_vehicle_marker.xml` draws on Android, and it is a
        // **template** image so `iconColor` can tint it with MAP-03's colour.
        let symbolConfiguration = UIImage.SymbolConfiguration(
            pointSize: DriverHomeMap.markerPointSize,
            weight: .bold
        )
        if let arrow = UIImage(systemName: DriverHomeMap.markerSymbol, withConfiguration: symbolConfiguration) {
            style.setImage(arrow.withRenderingMode(.alwaysTemplate), forName: DriverHomeMap.markerImage)
        }

        VehicleLayers.addVehicles(
            to: style,
            markerImage: DriverHomeMap.markerImage,
            clusterColour: UIColor(MageRideColor.primaryContainer),
            clusterTextColour: UIColor(MageRideColor.onPrimaryContainer)
        )
        VehicleLayers.addCircles(
            to: style,
            accuracyColour: UIColor(MageRideColor.primary),
            geofenceColour: UIColor(MageRideColor.success)
        )
        // MAP-03 as `VehicleLayers` builds it, rather than a flat tint: one expression means the
        // dashboard, My Vehicles and the passenger map cannot disagree about what colour a
        // three-wheeler is.
        (style.layer(withIdentifier: VehicleLayers.layerVehicles) as? MLNSymbolStyleLayer)?
            .iconColor = VehicleLayers.markerColourExpression()

        self.style = style
        mapView = map
        draw()
    }

    /// Replaces the whole vehicle source with the one feature this map is allowed to draw.
    ///
    /// A whole-collection set rather than an append: the source's contents *are* the driver's current
    /// position, and a source that accumulated would leave a trail of the driver's own history on
    /// their standby map.
    private func draw() {
        guard let style, let fix = position else { return }

        let vehicle = MLNPointFeature()
        vehicle.coordinate = CLLocationCoordinate2D(latitude: fix.lat, longitude: fix.lng)
        vehicle.attributes = [
            VehicleLayers.propVehicleType: (token ?? .privateHire).wire,
            VehicleLayers.propHeading: heading ?? Double(fix.headingDeg ?? 0),
        ]
        (style.source(withIdentifier: VehicleLayers.sourceVehicles) as? MLNShapeSource)?.shape =
            MLNShapeCollectionFeature(shapes: [vehicle])

        // MAP-02's accuracy circle and MAP-10's geofence share one source and are told apart by
        // `circleKind` — that is how `VehicleLayers.addCircles` filters its two layers.
        let accuracy = MLNPointFeature()
        accuracy.coordinate = vehicle.coordinate
        accuracy.attributes = [VehicleLayers.propCircleKind: VehicleLayers.circleAccuracy]

        var circles: [MLNShape] = [accuracy]
        if let geofence {
            let target = MLNPointFeature()
            target.coordinate = CLLocationCoordinate2D(latitude: geofence.lat, longitude: geofence.lng)
            target.attributes = [VehicleLayers.propCircleKind: VehicleLayers.circleGeofence]
            circles.append(target)
        }
        (style.source(withIdentifier: VehicleLayers.sourceCircles) as? MLNShapeSource)?.shape =
            MLNShapeCollectionFeature(shapes: circles)
    }

    /// D2' §0.3's recentre FAB. Does nothing before the first fix, which is the honest answer to
    /// "centre on me" when the handset does not yet know where that is.
    private func recentre() {
        guard let fix = position else { return }
        mapView?.setCenter(
            CLLocationCoordinate2D(latitude: fix.lat, longitude: fix.lng),
            zoomLevel: MapCamera.defaultZoom,
            animated: true
        )
    }

    /// A value that changes exactly when the drawn feature does.
    ///
    /// `Fix` is `Equatable` but `GeoPoint` is a Kotlin data class and is not, so `onChange(of:)` needs
    /// something `Hashable` built from the four inputs rather than the inputs themselves.
    private static func featureKey(_ fix: Fix?, _ token: VehicleToken?, _ heading: Double?, _ geofence: GeoPoint?)
        -> String {
        [
            fix.map { "\($0.lat),\($0.lng),\($0.headingDeg ?? -1)" } ?? "",
            token?.wire ?? "",
            // `String.init` unqualified is dozens of overloads, and offering the type checker all of
            // them inside an array literal it is already inferring is what produced a bare "failed
            // to produce diagnostic" here rather than an error anyone could act on. The closure
            // picks `String(_: Double)` and the expression checks in one step.
            heading.map { String($0) } ?? "",
            geofence.map { "\($0.lat),\($0.lng)" } ?? "",
        ].joined(separator: "|")
    }

    /// MAP-06's arrow. `location.north.fill` is the platform's own heading glyph and points up, which
    /// is what `iconRotation` needs — the same geometry `ic_vehicle_marker.xml` draws.
    private static let markerSymbol = "location.north.fill"

    /// The style image id the single `MLNSymbolStyleLayer` draws. Local to this screen; the shell has
    /// none.
    private static let markerImage = "mageride-own-vehicle"

    /// Big enough to read in a windscreen mount, small enough not to cover the junction it is at.
    private static let markerPointSize: CGFloat = 26
}
