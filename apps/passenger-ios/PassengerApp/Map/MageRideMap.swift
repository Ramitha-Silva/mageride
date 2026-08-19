import MageRideShared
import MapLibre
import SwiftUI
import UIKit

/// The passenger's own position, for MAP-02 and §0.3's blue user dot.
struct MapFix: Equatable {
    let lat: Double
    let lng: Double
    var accuracyMetres: Double = 0
}

/// A §0.3 pin. `kind` is one of ``VehicleLayers/pinPickup`` / `pinDropoff` / `pinUser`.
struct MapPin: Equatable {
    let kind: String
    let lat: Double
    let lng: Double
}

/// The map every screen with a map puts inside itself, with every MAP-* behaviour already in it.
///
/// D2' §0.1 and §0.3: MapLibre GL Native over self-served PMTiles, light and dark styles, no Google
/// Maps. **Unlike the driver app's equivalent — which hands a screen a bare `MLNStyle` and lets it
/// install what it needs, because AL-31 means there is only ever one marker on it — this widget owns
/// the whole §0.3 layer stack and draws from its parameters.** A passenger map that let each screen
/// install its own layers would be four screens deciding independently what a three-wheeler looks
/// like, which is exactly what MAP-03's legend exists to prevent. C076 made the same call on
/// Android; this is that decision kept.
///
/// A `UIViewRepresentable` rather than a SwiftUI `Map`: MapKit is not an option (§0.1), and
/// MapLibre's iOS SDK is UIKit.
///
/// - Parameters:
///   - vehicles: What the live plane says is nearby. Tweened through ``MarkerInterpolator`` —
///     MAP-04 — so a caller passes raw frames and never a rendering position.
///   - userPosition: MAP-02's accuracy circle and §0.3's blue dot. `nil` before the first fix.
///   - routePolyline: MAP-08's trip line, in order. Fewer than two points draws nothing.
///   - walkPolyline: SCR-PI-009's blue dashed leg from the passenger to the nearest halt. Empty
///     when they are already on the route, which is the common case.
///   - pins: §0.3's pickup and dropoff markers.
///   - geofence: MAP-10's 100 m circle, at the point being arrived at. `nil` everywhere else.
///   - camera: Where to open, and where to move when it changes.
///   - focus: A point the SCREEN wants the camera on, applied every time it becomes non-nil with a
///     value the map has not already flown to. Distinct from `camera`: that carries a zoom and is a
///     *state* the map is kept at, so driving it from a pin that the map itself moves would have the
///     camera fighting the pan that caused it. This is a one-shot request — set it, the map flies,
///     clear it on the next real pan. ``MapPickSheet``'s search is what needed one.
///   - onRecentre: §0.3's recentre FAB (*"both apps"*). `nil` hides it; a non-nil callback shows it,
///     and tapping it animates back to `userPosition` **here** before calling back — the caller has
///     no map handle to animate with, so a FAB that only called back would be an icon that promises
///     to recentre and does not.
///   - onCameraIdle: Where the map settled, after a pan or a zoom. This is how a centre-pin picker
///     works — SCR-PI-011's *"drag to adjust"* moves the **map** under a fixed marker.
///   - onVehicleTap: MAP-07 — the vehicle id under the tap. What a tap *opens* is the screen's:
///     SCR-PI-007 for Mode A, SCR-PI-024 for Mode B, nothing at all for an engaged Mode C
///     (AL-23, US-7.4).
///   - dimmed: SCR-PI-032 — what is drawn is **last known**, not live (US-15.2). The markers fade
///     rather than disappear, because a passenger who has lost signal still wants to know where the
///     bus was; the offline banner above says why they are faded.
struct MageRideMap: View {

    var vehicles: [MapVehicle] = []
    var userPosition: MapFix?
    var routePolyline: [GeoPoint] = []
    var walkPolyline: [GeoPoint] = []
    var pins: [MapPin] = []
    var geofence: GeoPoint?
    var camera: MapCamera = .colombo
    var focus: GeoPoint?
    var onRecentre: (() -> Void)?
    var onCameraIdle: ((GeoPoint) -> Void)?
    var onVehicleTap: ((String) -> Void)?
    var dimmed: Bool = false

    /// The PMTiles archive for this build. Read from the environment rather than taken as a
    /// parameter so no screen has to remember to pass it.
    @Environment(\.pmTilesUrl) private var pmTilesUrl

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            MapLibreHost(
                vehicles: vehicles,
                userPosition: userPosition,
                routePolyline: routePolyline,
                walkPolyline: walkPolyline,
                pins: pins,
                geofence: geofence,
                camera: camera,
                focus: focus,
                pmTilesUrl: pmTilesUrl,
                onCameraIdle: onCameraIdle,
                onVehicleTap: onVehicleTap,
                dimmed: dimmed
            )
            .accessibilityLabel(Text("map_content_description", bundle: MageRideColor.bundle))

            if let onRecentre {
                MapRecentreButton {
                    NotificationCenter.default.post(name: .mageRideMapRecentre, object: userPosition)
                    onRecentre()
                }
                .padding(MageRideSpacing.sm)
            }
        }
    }
}

/// D2' §0.3's recentre control — *"Recentre FAB both apps"*.
///
/// A circular button on the map's own surface, at the HIG tap-target floor. The label is trilingual
/// and is read by VoiceOver; the glyph alone would be a control with no name.
struct MapRecentreButton: View {

    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "location.fill")
                .font(.system(size: MageRideControl.rowIcon, weight: .semibold))
                .foregroundStyle(MageRideColor.primary)
                .frame(width: MageRideControl.mapControl, height: MageRideControl.mapControl)
                .background(MageRideColor.background, in: Circle())
                .mageElevated()
        }
        .accessibilityLabel(Text("map_recentre", bundle: MageRideColor.bundle))
    }
}

extension Notification.Name {
    /// How the recentre button reaches the `MLNMapView` it is drawn over.
    ///
    /// The button is a SwiftUI view beside the representable, so it has no handle on the map. A
    /// notification rather than a `Binding<MapCamera>` the caller has to write: recentring is a
    /// *gesture*, not a state change, and a camera binding would make "the passenger panned away"
    /// and "the passenger asked to come back" the same event.
    static let mageRideMapRecentre = Notification.Name("lk.mageride.passenger.map.recentre")
}

/// The PMTiles archive URL, in the environment.
private struct PMTilesUrlKey: EnvironmentKey {
    static let defaultValue = ""
}

extension EnvironmentValues {
    var pmTilesUrl: String {
        get { self[PMTilesUrlKey.self] }
        set { self[PMTilesUrlKey.self] = newValue }
    }
}

/// The `MLNMapView` itself.
///
/// Split from ``MageRideMap`` so the SwiftUI parameters and the UIKit lifetime are separate
/// problems: this type is the whole of what touches MapLibre.
private struct MapLibreHost: UIViewRepresentable {

    let vehicles: [MapVehicle]
    let userPosition: MapFix?
    let routePolyline: [GeoPoint]
    let walkPolyline: [GeoPoint]
    let pins: [MapPin]
    let geofence: GeoPoint?
    let camera: MapCamera
    let focus: GeoPoint?
    let pmTilesUrl: String
    let onCameraIdle: ((GeoPoint) -> Void)?
    let onVehicleTap: ((String) -> Void)?
    let dimmed: Bool

    @Environment(\.colorScheme) private var colorScheme

    func makeUIView(context: Context) -> MLNMapView {
        let view = MLNMapView(frame: .zero)
        view.delegate = context.coordinator
        // §0.3's user dot is drawn from the app's own fix, in the same source MAP-02's halo is, so
        // MapLibre's location puck is off: two blue dots that disagree by a fix is worse than one.
        view.showsUserLocation = false
        view.logoView.isHidden = false
        view.attributionButton.isHidden = false
        view.setCenter(
            CLLocationCoordinate2D(latitude: camera.lat, longitude: camera.lng),
            zoomLevel: camera.zoom,
            animated: false
        )

        let tap = UITapGestureRecognizer(
            target: context.coordinator,
            action: #selector(Coordinator.handleTap(_:))
        )
        view.addGestureRecognizer(tap)

        context.coordinator.attach(to: view)
        applyStyle(to: view, coordinator: context.coordinator)
        return view
    }

    func updateUIView(_ view: MLNMapView, context: Context) {
        let coordinator = context.coordinator
        coordinator.onVehicleTap = onVehicleTap
        coordinator.onCameraIdle = onCameraIdle

        if coordinator.appliedDarkMode != (colorScheme == .dark) {
            applyStyle(to: view, coordinator: coordinator)
        }
        if coordinator.appliedCamera != camera {
            view.setCenter(
                CLLocationCoordinate2D(latitude: camera.lat, longitude: camera.lng),
                zoomLevel: camera.zoom,
                animated: true
            )
            coordinator.appliedCamera = camera
        }

        // The screen asking for a move — see `focus`. Compared on the coordinates rather than on
        // the object, and forgotten as soon as the request is cleared, which is what lets the same
        // place be asked for twice: a passenger who taps a result, pans away and taps it again is
        // asking a second time, and the second ask must move the map as the first one did.
        if let focus {
            if !coordinator.hasFlownTo(focus) {
                view.setCenter(
                    CLLocationCoordinate2D(latitude: focus.lat, longitude: focus.lng),
                    zoomLevel: MapCamera.defaultZoom,
                    animated: true
                )
                coordinator.rememberFlight(to: focus)
            }
        } else {
            coordinator.forgetFlight()
        }

        coordinator.draw(
            vehicles: vehicles,
            userPosition: userPosition,
            routePolyline: routePolyline,
            walkPolyline: walkPolyline,
            pins: pins,
            geofence: geofence,
            dimmed: dimmed
        )
    }

    static func dismantleUIView(_ view: MLNMapView, coordinator: Coordinator) {
        coordinator.detach()
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(darkMode: colorScheme == .dark)
    }

    private func applyStyle(to view: MLNMapView, coordinator: Coordinator) {
        guard let url = try? MapStyles.url(darkMode: colorScheme == .dark, pmTilesUrl: pmTilesUrl) else { return }
        coordinator.palette = MapPalette.resolved(darkMode: colorScheme == .dark)
        view.styleURL = url
    }

    /// Everything that outlives a `body` evaluation: the interpolator's tracks, the display link,
    /// the last-applied camera and the two callbacks the MapLibre delegate reads.
    final class Coordinator: NSObject, MLNMapViewDelegate {

        var palette: MapPalette
        var appliedDarkMode: Bool?
        var appliedCamera: MapCamera?
        var onVehicleTap: ((String) -> Void)?
        var onCameraIdle: ((GeoPoint) -> Void)?

        /// The last point a `focus` request flew to, as coordinates rather than as the object.
        ///
        /// `GeoPoint` crosses from Kotlin, so comparing two of them is comparing whatever the
        /// bridge decided `isEqual:` means; two `Double`s cannot be wrong about it.
        private var flownToLat: Double?
        private var flownToLng: Double?

        func hasFlownTo(_ point: GeoPoint) -> Bool {
            flownToLat == point.lat && flownToLng == point.lng
        }

        func rememberFlight(to point: GeoPoint) {
            flownToLat = point.lat
            flownToLng = point.lng
        }

        func forgetFlight() {
            flownToLat = nil
            flownToLng = nil
        }

        private weak var mapView: MLNMapView?
        private var style: MLNStyle?
        private let interpolator = MarkerInterpolator()
        private var displayLink: CADisplayLink?
        private var accuracyMetres: Double = 0
        private var pending = Pending()
        private var recentreObserver: NSObjectProtocol?

        init(darkMode: Bool) {
            self.palette = MapPalette.resolved(darkMode: darkMode)
        }

        func attach(to view: MLNMapView) {
            mapView = view
            recentreObserver = NotificationCenter.default.addObserver(
                forName: .mageRideMapRecentre,
                object: nil,
                queue: .main
            ) { [weak self] note in
                guard let fix = note.object as? MapFix else { return }
                self?.mapView?.setCenter(
                    CLLocationCoordinate2D(latitude: fix.lat, longitude: fix.lng),
                    zoomLevel: MapCamera.defaultZoom,
                    animated: true
                )
            }
        }

        func detach() {
            displayLink?.invalidate()
            displayLink = nil
            if let recentreObserver { NotificationCenter.default.removeObserver(recentreObserver) }
        }

        deinit {
            displayLink?.invalidate()
            if let recentreObserver { NotificationCenter.default.removeObserver(recentreObserver) }
        }

        // MARK: - Style

        /// **Fires on every style load, not just the first.** Switching between light and dark
        /// re-loads the style and drops every layer and every source, so everything is installed
        /// here and the last-drawn contents are re-applied behind it — otherwise a passenger who
        /// walks into a tunnel at dusk watches every marker vanish.
        func mapView(_ mapView: MLNMapView, didFinishLoading style: MLNStyle) {
            appliedDarkMode = mapView.traitCollection.userInterfaceStyle == .dark
            self.style = style

            style.setImage(Coordinator.markerImage(), forName: VehicleLayers.markerImage)
            VehicleLayers.install(on: style, markerImage: VehicleLayers.markerImage, palette: palette)

            redraw()
            rescaleCircles()
        }

        // MARK: - Contents

        func draw(
            vehicles: [MapVehicle],
            userPosition: MapFix?,
            routePolyline: [GeoPoint],
            walkPolyline: [GeoPoint],
            pins: [MapPin],
            geofence: GeoPoint?,
            dimmed: Bool
        ) {
            let vehiclesChanged = pending.vehicles != vehicles
            pending = Pending(
                vehicles: vehicles,
                userPosition: userPosition,
                routePolyline: routePolyline,
                walkPolyline: walkPolyline,
                pins: pins,
                geofence: geofence,
                dimmed: dimmed
            )
            accuracyMetres = userPosition?.accuracyMetres ?? 0

            if vehiclesChanged {
                // MAP-04. Seating a batch restarts the glide; the display link runs until every
                // track has landed and then stops, rather than holding the render loop awake for a
                // map nothing is moving on.
                interpolator.onFrames(vehicles, now: CACurrentMediaTime())
                startDisplayLink()
            }
            redraw()
        }

        private func redraw() {
            guard let style else { return }
            drawVehicles(on: style, at: CACurrentMediaTime())
            drawCircles(on: style)
            drawLine(on: style, source: VehicleLayers.sourceRoute, polyline: pending.routePolyline)
            drawLine(on: style, source: VehicleLayers.sourceWalk, polyline: pending.walkPolyline)
            drawPins(on: style)
            setVehicleOpacity(on: style, opacity: pending.dimmed ? VehicleLayers.staleOpacity : 1)
            rescaleCircles()
        }

        private func drawVehicles(on style: MLNStyle, at now: TimeInterval) {
            guard let source = style.source(withIdentifier: VehicleLayers.sourceVehicles) as? MLNShapeSource else {
                return
            }
            let features: [MLNPointFeature] = interpolator.markers(at: now).map { marker in
                let feature = MLNPointFeature()
                feature.coordinate = CLLocationCoordinate2D(latitude: marker.lat, longitude: marker.lng)
                feature.attributes = [
                    VehicleLayers.propVehicleId: marker.vehicleId,
                    VehicleLayers.propVehicleType: marker.type ?? "",
                    VehicleLayers.propHeading: marker.heading,
                ]
                return feature
            }
            source.shape = MLNShapeCollectionFeature(shapes: features)
        }

        private func drawCircles(on style: MLNStyle) {
            guard let source = style.source(withIdentifier: VehicleLayers.sourceCircles) as? MLNShapeSource else {
                return
            }
            var features: [MLNPointFeature] = []
            if let fix = pending.userPosition {
                let feature = MLNPointFeature()
                feature.coordinate = CLLocationCoordinate2D(latitude: fix.lat, longitude: fix.lng)
                feature.attributes = [VehicleLayers.propCircleKind: VehicleLayers.circleAccuracy]
                features.append(feature)
            }
            if let geofence = pending.geofence {
                let feature = MLNPointFeature()
                feature.coordinate = CLLocationCoordinate2D(latitude: geofence.lat, longitude: geofence.lng)
                feature.attributes = [VehicleLayers.propCircleKind: VehicleLayers.circleGeofence]
                features.append(feature)
            }
            source.shape = MLNShapeCollectionFeature(shapes: features)
        }

        /// MAP-08's trip line and SCR-PI-009's walk-to-halt leg are the same operation on two
        /// sources — the difference between them is entirely in how ``VehicleLayers`` styles each.
        private func drawLine(on style: MLNStyle, source id: String, polyline: [GeoPoint]) {
            guard let source = style.source(withIdentifier: id) as? MLNShapeSource else { return }
            // Two points is the minimum a line accepts; anything less is "no route yet", which is an
            // empty collection rather than a degenerate line the renderer would silently drop.
            guard polyline.count >= 2 else {
                source.shape = MLNShapeCollectionFeature(shapes: [])
                return
            }
            var coordinates = polyline.map { CLLocationCoordinate2D(latitude: $0.lat, longitude: $0.lng) }
            let line = MLNPolylineFeature(coordinates: &coordinates, count: UInt(coordinates.count))
            source.shape = MLNShapeCollectionFeature(shapes: [line])
        }

        private func drawPins(on style: MLNStyle) {
            guard let source = style.source(withIdentifier: VehicleLayers.sourcePins) as? MLNShapeSource else {
                return
            }
            var features: [MLNPointFeature] = pending.pins.map { pin in
                let feature = MLNPointFeature()
                feature.coordinate = CLLocationCoordinate2D(latitude: pin.lat, longitude: pin.lng)
                feature.attributes = [VehicleLayers.propPinKind: pin.kind]
                return feature
            }
            // §0.3's "user blue dot" is the passenger's own position, so it is drawn from the same
            // fix MAP-02's halo is — one source of truth for where the passenger is, rather than a
            // screen remembering to pass its own position twice.
            if let fix = pending.userPosition {
                let feature = MLNPointFeature()
                feature.coordinate = CLLocationCoordinate2D(latitude: fix.lat, longitude: fix.lng)
                feature.attributes = [VehicleLayers.propPinKind: VehicleLayers.pinUser]
                features.append(feature)
            }
            source.shape = MLNShapeCollectionFeature(shapes: features)
        }

        /// Fades the vehicle symbols and their clusters together, so a cluster cannot outshine its
        /// pins. Opacity rather than removal (SCR-PI-032): the markers are the last thing the
        /// platform said, and erasing them would answer *"where was my bus?"* with nothing at all.
        private func setVehicleOpacity(on style: MLNStyle, opacity: CGFloat) {
            let value = NSExpression(forConstantValue: opacity)
            (style.layer(withIdentifier: VehicleLayers.layerVehicles) as? MLNSymbolStyleLayer)?.iconOpacity = value
            (style.layer(withIdentifier: VehicleLayers.layerClusters) as? MLNCircleStyleLayer)?.circleOpacity = value
            (style.layer(withIdentifier: VehicleLayers.layerClusterCount) as? MLNSymbolStyleLayer)?.textOpacity = value
        }

        // MARK: - Metres versus pixels

        /// **MAP-02 and MAP-10 are drawn in metres and MapLibre's circle radius is a screen
        /// measurement**, so both layers are re-scaled whenever the camera settles.
        /// `metersPerPoint(atLatitude:)` is the projection's own answer at the current zoom, which is
        /// what makes a circle cover the ground it claims to at every zoom level — the entire point
        /// of a *100 m* geofence and of an accuracy halo. A radius set once is wrong everywhere else.
        ///
        /// On idle rather than every frame, deliberately: the conversion needs a latitude and a zoom
        /// and both are stable between gestures.
        private func rescaleCircles() {
            guard let style, let mapView else { return }
            let metresPerPoint = mapView.metersPerPoint(atLatitude: mapView.centerCoordinate.latitude)
            guard metresPerPoint > 0 else { return }

            (style.layer(withIdentifier: VehicleLayers.layerAccuracy) as? MLNCircleStyleLayer)?.circleRadius =
                NSExpression(forConstantValue: accuracyMetres / metresPerPoint)
            (style.layer(withIdentifier: VehicleLayers.layerGeofence) as? MLNCircleStyleLayer)?.circleRadius =
                NSExpression(forConstantValue: VehicleLayers.geofenceRadiusMetres / metresPerPoint)
        }

        func mapViewRegionIsChanging(_ mapView: MLNMapView) {}

        func mapView(_ mapView: MLNMapView, regionDidChangeAnimated animated: Bool) {
            rescaleCircles()
            // The same callback serves the centre-pin pickers: settling IS the gesture ending, and
            // reporting every intermediate frame would fire a reverse geocode per point of pan.
            let centre = mapView.centerCoordinate
            onCameraIdle?(GeoPoint(lat: centre.latitude, lng: centre.longitude))
        }

        // MARK: - MAP-07

        /// The vehicle id under the tap, or nothing when the tap missed.
        ///
        /// Hit-testing is the map's, so every screen that draws a vehicle can offer the tap; what it
        /// opens is the screen's own decision. Only the symbol layer is queried — a tap on a
        /// *cluster* is not a vehicle, and answering one with an arbitrary member would open a popup
        /// about a bus the passenger did not aim at.
        @objc func handleTap(_ recogniser: UITapGestureRecognizer) {
            guard let mapView, let onVehicleTap else { return }
            let point = recogniser.location(in: mapView)
            let features = mapView.visibleFeatures(
                at: point,
                styleLayerIdentifiers: [VehicleLayers.layerVehicles]
            )
            guard let id = features.first?.attribute(forKey: VehicleLayers.propVehicleId) as? String else { return }
            onVehicleTap(id)
        }

        // MARK: - MAP-04's frame loop

        private func startDisplayLink() {
            guard displayLink == nil else { return }
            let link = CADisplayLink(target: self, selector: #selector(step))
            link.add(to: .main, forMode: .common)
            displayLink = link
        }

        @objc private func step() {
            let now = CACurrentMediaTime()
            if let style { drawVehicles(on: style, at: now) }
            if interpolator.isSettled(at: now) {
                displayLink?.invalidate()
                displayLink = nil
            }
        }

        // MARK: -

        /// MAP-06's arrow, rendered from an SF Symbol.
        ///
        /// **The rendering mode is load-bearing.** MapLibre tints a style image with `iconColor`
        /// only when it is a *template*; a non-template image is drawn as-is, and MAP-03's whole
        /// eleven-colour legend would silently render in one colour. That is the failure mode this
        /// comment exists for.
        ///
        /// A chevron rather than a vehicle glyph: this one image is rotated to every vehicle's
        /// heading and tinted to its type, so it has to read as a direction. §0.2's per-type SF
        /// Symbols are for the *cards* — SCR-PI-006's filter, SCR-PI-007's popup, SCR-PI-009's tier
        /// rows — where there is room for a silhouette. See ``VehicleToken/symbolName``.
        static func markerImage() -> UIImage {
            let configuration = UIImage.SymbolConfiguration(pointSize: 18, weight: .bold)
            let symbol = UIImage(systemName: "location.north.fill", withConfiguration: configuration)
            return (symbol ?? UIImage()).withRenderingMode(.alwaysTemplate)
        }

        private struct Pending {
            var vehicles: [MapVehicle] = []
            var userPosition: MapFix?
            var routePolyline: [GeoPoint] = []
            var walkPolyline: [GeoPoint] = []
            var pins: [MapPin] = []
            var geofence: GeoPoint?
            var dimmed = false
        }
    }
}
