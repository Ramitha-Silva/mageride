import MapLibre
import SwiftUI

/// The MapLibre host every map screen sits on.
///
/// D2' §C's map row is "MapLibre GL Native (Android SDK) / **MapLibre GL Native (iOS SDK)**", and
/// §0.1 fixes the source: self-served PMTiles on R2, no Google Maps. `pmtiles://` is a scheme
/// MapLibre Native resolves itself — the PMTiles file source is compiled into the shipped library,
/// so there is no protocol shim to register and no HTTP range-request code here. That is the whole
/// reason PMTiles was chosen over a tile server.
///
/// A `UIViewRepresentable` rather than a SwiftUI `Map`: MapKit is not an option (§0.1), and
/// MapLibre's iOS SDK is UIKit. The wrapper is deliberately thin — it owns the view's lifetime, the
/// style for the appearance in force and the camera, and hands the `MLNStyle` to the screen through
/// ``onStyleLoaded`` so a screen installs its own layers exactly as it does on Android.
///
/// **The recentre control is the screen's, not this view's.** D2' §0.3 puts a recentre FAB on both
/// apps, but where it sits differs per screen (the dashboard has a bottom sheet under it), so the
/// shell provides ``MapRecentreButton`` and the screen places it.
struct MageRideMapView: UIViewRepresentable {

    /// Where the camera sits. Changing it moves the camera; the user moving the map does not write
    /// back — a screen that wants to follow the driver sets this on every fix.
    var camera: MapCamera

    /// The PMTiles archive for this build. From ``DriverEnvironment``.
    var pmTilesUrl: String

    /// Called once per style load with the loaded style, on the main thread.
    ///
    /// **Every style load, not just the first.** Switching between light and dark re-loads the
    /// style and drops every layer a screen added, so a screen installs its layers here rather than
    /// in `onAppear`.
    var onStyleLoaded: (MLNStyle, MLNMapView) -> Void = { _, _ in }

    @Environment(\.colorScheme) private var colorScheme

    func makeUIView(context: Context) -> MLNMapView {
        let view = MLNMapView(frame: .zero)
        view.delegate = context.coordinator
        // The driver's own vehicle is drawn by the screen from the app's own GNSS stream, so
        // MapLibre's location puck is off: two blue dots that disagree by a fix is worse than one.
        view.showsUserLocation = false
        view.logoView.isHidden = false
        view.attributionButton.isHidden = false
        view.setCenter(
            CLLocationCoordinate2D(latitude: camera.lat, longitude: camera.lng),
            zoomLevel: camera.zoom,
            animated: false
        )
        applyStyle(to: view)
        return view
    }

    func updateUIView(_ view: MLNMapView, context: Context) {
        if context.coordinator.appliedDarkMode != (colorScheme == .dark) {
            applyStyle(to: view)
        }
        if context.coordinator.appliedCamera != camera {
            view.setCenter(
                CLLocationCoordinate2D(latitude: camera.lat, longitude: camera.lng),
                zoomLevel: camera.zoom,
                animated: true
            )
            context.coordinator.appliedCamera = camera
        }
        context.coordinator.onStyleLoaded = onStyleLoaded
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(onStyleLoaded: onStyleLoaded)
    }

    private func applyStyle(to view: MLNMapView) {
        guard let url = try? MapStyles.url(darkMode: colorScheme == .dark, pmTilesUrl: pmTilesUrl) else { return }
        view.styleURL = url
    }

    final class Coordinator: NSObject, MLNMapViewDelegate {
        var onStyleLoaded: (MLNStyle, MLNMapView) -> Void
        var appliedDarkMode: Bool?
        var appliedCamera: MapCamera?

        init(onStyleLoaded: @escaping (MLNStyle, MLNMapView) -> Void) {
            self.onStyleLoaded = onStyleLoaded
        }

        func mapView(_ mapView: MLNMapView, didFinishLoading style: MLNStyle) {
            appliedDarkMode = mapView.traitCollection.userInterfaceStyle == .dark
            onStyleLoaded(style, mapView)
        }
    }
}

/// D2' §0.3's recentre control — "Recentre FAB both apps".
///
/// A circular button on the map's own surface, at the HIG tap-target floor. The label is trilingual
/// and is read by VoiceOver; the glyph alone would be a control with no name.
struct MapRecentreButton: View {

    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "location.fill")
                .font(.system(size: 18, weight: .semibold))
                .foregroundStyle(MageRideColor.primary)
                .frame(width: MageRideControl.mapControl, height: MageRideControl.mapControl)
                .background(MageRideColor.background, in: Circle())
                .mageElevated()
        }
        .accessibilityLabel(Text("map_recentre", bundle: MageRideColor.bundle))
    }
}
