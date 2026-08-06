package lk.mageride.passenger.map

import android.os.SystemClock
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MyLocation
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import lk.mageride.passenger.R
import lk.mageride.passenger.di.PassengerEnvironment
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import org.koin.compose.koinInject
import org.maplibre.android.MapLibre
import org.maplibre.android.camera.CameraPosition
import org.maplibre.android.camera.CameraUpdateFactory
import org.maplibre.android.geometry.LatLng
import org.maplibre.android.maps.MapLibreMap
import org.maplibre.android.maps.MapView
import org.maplibre.android.maps.Style
import org.maplibre.android.style.layers.CircleLayer
import org.maplibre.android.style.layers.PropertyFactory
import org.maplibre.android.style.sources.GeoJsonSource
import org.maplibre.geojson.Feature
import org.maplibre.geojson.FeatureCollection
import org.maplibre.geojson.LineString
import org.maplibre.geojson.Point

/** The passenger's own position, for MAP-02 and §0.3's blue user dot. */
internal data class MapFix(val lat: Double, val lng: Double, val accuracyMetres: Double = 0.0)

/** A §0.3 pin. [kind] is one of `VehicleLayers.PIN_PICKUP` / `PIN_DROPOFF` / `PIN_USER`. */
internal data class MapPin(val kind: String, val lat: Double, val lng: Double)

/**
 * The map every screen with a map puts inside itself, with every MAP-* behaviour already in it.
 *
 * D2' §0.1 and §0.3: MapLibre GL Native over self-served PMTiles, light and dark styles, no Google
 * Maps. Unlike the driver app's equivalent — which hands a screen a bare `Style` and lets it
 * install what it needs, because AL-31 means there is only ever one marker on it — this widget
 * owns the whole §0.3 layer stack and draws from its parameters. A passenger map that let each
 * screen install its own layers would be four screens deciding independently what a three-wheeler
 * looks like, which is exactly what MAP-03's legend exists to prevent.
 *
 * It also owns the `MapView`'s lifecycle, which is the other reason it exists: `MapView` predates
 * Compose and needs all of its lifecycle callbacks forwarded or it leaks its GL surface on the
 * first rotation.
 *
 * @param vehicles What the live plane says is nearby. Tweened through [MarkerInterpolator] —
 *   MAP-04 — so a caller passes raw frames and never a rendering position.
 * @param userPosition MAP-02's accuracy circle and §0.3's blue dot. `null` before the first fix.
 * @param routePolyline MAP-08's trip line, in order. Empty draws nothing.
 * @param pins §0.3's pickup and dropoff markers.
 * @param geofence MAP-10's 100 m circle, at the point being arrived at. `null` everywhere else.
 * @param camera Where to open. A screen that tracks the passenger moves the camera itself after.
 * @param onRecentre §0.3's recentre FAB ("both apps"). `null` hides it.
 * @param onVehicleTap MAP-07 — the vehicle id under the tap, or nothing when the tap missed. What
 *   a tap *opens* is the screen's: SCR-PA-007 for Mode A, SCR-PA-024 for Mode B, nothing at all
 *   for an engaged Mode C (AL-23, US-7.4).
 */
@Composable
@Suppress("LongParameterList") // Every parameter is one row of D2' §0.3's layer list.
internal fun MageRideMap(
    modifier: Modifier = Modifier,
    vehicles: List<MapVehicle> = emptyList(),
    userPosition: MapFix? = null,
    routePolyline: List<GeoPoint> = emptyList(),
    pins: List<MapPin> = emptyList(),
    geofence: GeoPoint? = null,
    camera: MapCamera = MapCamera.Default,
    darkTheme: Boolean = isSystemInDarkTheme(),
    onRecentre: (() -> Unit)? = null,
    onVehicleTap: ((String) -> Unit)? = null,
) {
    val context = LocalContext.current
    val environment = koinInject<PassengerEnvironment>()
    val description = stringResource(R.string.map_content_description)

    val palette = mapPalette

    var map by remember { mutableStateOf<MapLibreMap?>(null) }
    var style by remember { mutableStateOf<Style?>(null) }

    // MAP-04's tracks, and the accuracy the circle layers were last scaled for. Both are held as
    // remembered objects rather than as file-level state because the MapLibre listeners below are
    // registered once and outlive every recomposition: they can capture a stable reference, but
    // they cannot re-read a captured *value*.
    val interpolator = remember { MarkerInterpolator() }
    val accuracyMetres = remember { mutableStateOf(0.0) }

    // Read inside the MapLibre click callback, which outlives the composition that installed it.
    val currentOnVehicleTap by rememberUpdatedState(onVehicleTap)

    // MapLibre.getInstance loads the native library and sets up the file source. Idempotent, and
    // called here rather than in Application.onCreate so a passenger who never opens a map never
    // pays for the .so.
    remember { MapLibre.getInstance(context) }

    val mapView = remember {
        MapView(context).apply {
            getMapAsync { ready ->
                ready.setStyle(
                    Style.Builder().fromJson(MapStyles.forTheme(context, environment.pmTilesUrl, darkTheme)),
                ) { readyStyle ->
                    ready.cameraPosition = CameraPosition.Builder()
                        .target(LatLng(camera.lat, camera.lng))
                        .zoom(camera.zoom)
                        .build()

                    ContextCompat.getDrawable(context, R.drawable.ic_vehicle_marker)?.let { drawable ->
                        readyStyle.addImage(MARKER_IMAGE, drawable.toBitmap())
                    }

                    VehicleLayers.install(readyStyle, MARKER_IMAGE, palette)

                    // MAP-07. Hit-testing is the map's, so every screen that draws a vehicle can
                    // offer the tap; what it opens is the screen's own decision.
                    ready.addOnMapClickListener { point ->
                        val tapped = ready.vehicleIdAt(point)
                        if (tapped != null) currentOnVehicleTap?.invoke(tapped)
                        tapped != null
                    }

                    // MAP-02 and MAP-10 are drawn in METRES and MapLibre's circle radius is a
                    // SCREEN measurement, so the two layers are re-scaled whenever the camera
                    // settles. Doing it on idle rather than on every frame is deliberate: the
                    // conversion needs a latitude and a zoom, and both are stable between gestures.
                    ready.addOnCameraIdleListener { readyStyle.rescaleCircles(ready, accuracyMetres.value) }

                    map = ready
                    style = readyStyle
                }
            }
        }
    }

    MapViewLifecycle(mapView)

    // MAP-04. Restarting on each new batch is what makes the loop finite: the effect seats the
    // targets, animates until every track has landed, and then suspends until the next batch
    // rather than holding the choreographer awake for a map nothing is moving on.
    LaunchedEffect(style, vehicles) {
        val installed = style ?: return@LaunchedEffect
        interpolator.onFrames(vehicles, SystemClock.uptimeMillis())
        do {
            val now = SystemClock.uptimeMillis()
            installed.drawVehicles(interpolator.markersAt(now))
            val settled = interpolator.isSettled(now)
            if (!settled) withFrameNanos { }
        } while (!settled)
    }

    LaunchedEffect(style, userPosition, geofence, map) {
        val installed = style ?: return@LaunchedEffect
        accuracyMetres.value = userPosition?.accuracyMetres ?: 0.0
        installed.drawCircles(userPosition, geofence)
        map?.let { installed.rescaleCircles(it, accuracyMetres.value) }
    }

    LaunchedEffect(style, routePolyline) {
        style?.drawRoute(routePolyline)
    }

    LaunchedEffect(style, pins, userPosition) {
        style?.drawPins(pins, userPosition)
    }

    Box(modifier = modifier) {
        AndroidView(
            factory = { mapView },
            modifier = Modifier
                .fillMaxSize()
                .semantics { contentDescription = description },
        )

        if (onRecentre != null) {
            FloatingActionButton(
                onClick = onRecentre,
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(MageRideTheme.spacing.sm),
                containerColor = MaterialTheme.colorScheme.surface,
                contentColor = MaterialTheme.colorScheme.onSurface,
            ) {
                Icon(
                    imageVector = Icons.Filled.MyLocation,
                    contentDescription = stringResource(R.string.map_recentre),
                )
            }
        }
    }
}

/** Moves the camera to [target], animated. The recentre FAB's action, and a tracking screen's tick. */
internal fun MapLibreMap.centreOn(target: LatLng, zoom: Double = MapCamera.DEFAULT_ZOOM) {
    animateCamera(CameraUpdateFactory.newLatLngZoom(target, zoom))
}

/** The style image id the vehicle `SymbolLayer` draws — MAP-06's heading arrow. */
private const val MARKER_IMAGE = "mageride-vehicle"

private fun Style.drawVehicles(markers: List<MapVehicle>) {
    val source = getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_VEHICLES) ?: return
    val features = markers.map { marker ->
        Feature.fromGeometry(Point.fromLngLat(marker.lng, marker.lat)).apply {
            addStringProperty(VehicleLayers.PROP_VEHICLE_ID, marker.vehicleId)
            addStringProperty(VehicleLayers.PROP_VEHICLE_TYPE, marker.type?.wire.orEmpty())
            addNumberProperty(VehicleLayers.PROP_HEADING, marker.heading)
        }
    }
    source.setGeoJson(FeatureCollection.fromFeatures(features))
}

private fun Style.drawCircles(fix: MapFix?, geofence: GeoPoint?) {
    val source = getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_CIRCLES) ?: return
    val features = listOfNotNull(
        fix?.let {
            Feature.fromGeometry(Point.fromLngLat(it.lng, it.lat)).apply {
                addStringProperty(VehicleLayers.PROP_CIRCLE_KIND, VehicleLayers.CIRCLE_ACCURACY)
            }
        },
        geofence?.let {
            Feature.fromGeometry(Point.fromLngLat(it.lng, it.lat)).apply {
                addStringProperty(VehicleLayers.PROP_CIRCLE_KIND, VehicleLayers.CIRCLE_GEOFENCE)
            }
        },
    )
    source.setGeoJson(FeatureCollection.fromFeatures(features))
}

private fun Style.drawRoute(polyline: List<GeoPoint>) {
    val source = getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_ROUTE) ?: return
    // Two points is the minimum a LineString accepts; anything less is "no route yet", which is
    // an empty collection rather than a degenerate line the renderer would silently drop.
    val features = if (polyline.size < MIN_LINE_POINTS) {
        emptyList()
    } else {
        listOf(Feature.fromGeometry(LineString.fromLngLats(polyline.map { Point.fromLngLat(it.lng, it.lat) })))
    }
    source.setGeoJson(FeatureCollection.fromFeatures(features))
}

private fun Style.drawPins(pins: List<MapPin>, fix: MapFix?) {
    val source = getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_PINS) ?: return
    val features = buildList {
        pins.forEach { pin ->
            add(
                Feature.fromGeometry(Point.fromLngLat(pin.lng, pin.lat)).apply {
                    addStringProperty(VehicleLayers.PROP_PIN_KIND, pin.kind)
                },
            )
        }
        // §0.3's "user blue dot" is the passenger's own position, so it is drawn from the same fix
        // MAP-02's halo is — one source of truth for where the passenger is, rather than a screen
        // remembering to pass its own position twice.
        fix?.let {
            add(
                Feature.fromGeometry(Point.fromLngLat(it.lng, it.lat)).apply {
                    addStringProperty(VehicleLayers.PROP_PIN_KIND, VehicleLayers.PIN_USER)
                },
            )
        }
    }
    source.setGeoJson(FeatureCollection.fromFeatures(features))
}

/**
 * Converts MAP-02's and MAP-10's metres into the screen radius MapLibre wants.
 *
 * `getMetersPerPixelAtLatitude` is the projection's own answer at the current zoom, so a circle
 * drawn through it covers the ground it claims to at every zoom level — which is the entire point
 * of a *100 m* geofence and of an accuracy halo.
 */
private fun Style.rescaleCircles(map: MapLibreMap, accuracyMetres: Double) {
    val latitude = map.cameraPosition.target?.latitude ?: return
    val metresPerPixel = map.projection.getMetersPerPixelAtLatitude(latitude)
    if (metresPerPixel <= 0.0) return

    getLayerAs<CircleLayer>(VehicleLayers.LAYER_ACCURACY)
        ?.setProperties(PropertyFactory.circleRadius((accuracyMetres / metresPerPixel).toFloat()))
    getLayerAs<CircleLayer>(VehicleLayers.LAYER_GEOFENCE)
        ?.setProperties(
            PropertyFactory.circleRadius((VehicleLayers.GEOFENCE_RADIUS_M / metresPerPixel).toFloat()),
        )
}

/** MAP-07's hit test — the vehicle id under [point], or `null` when the tap missed every marker. */
private fun MapLibreMap.vehicleIdAt(point: LatLng): String? =
    queryRenderedFeatures(projection.toScreenLocation(point), VehicleLayers.LAYER_VEHICLES)
        .firstOrNull()
        ?.getStringProperty(VehicleLayers.PROP_VEHICLE_ID)

private const val MIN_LINE_POINTS = 2

/**
 * Forwards the Android lifecycle into [MapView].
 *
 * Every callback, not a subset: `onStart`/`onStop` bind and release the GL context, `onPause`
 * stops the render loop, and `onDestroy` frees the native map. Missing `onDestroy` alone leaks a
 * few megabytes of native memory per rotation, which on a five-year-old handset is a crash within
 * an afternoon.
 */
@Composable
private fun MapViewLifecycle(mapView: MapView) {
    val lifecycleOwner = LocalLifecycleOwner.current

    DisposableEffect(lifecycleOwner, mapView) {
        mapView.onCreate(null)

        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> mapView.onStart()
                Lifecycle.Event.ON_RESUME -> mapView.onResume()
                Lifecycle.Event.ON_PAUSE -> mapView.onPause()
                Lifecycle.Event.ON_STOP -> mapView.onStop()
                Lifecycle.Event.ON_DESTROY -> mapView.onDestroy()
                else -> Unit
            }
        }

        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            mapView.onDestroy()
        }
    }
}
