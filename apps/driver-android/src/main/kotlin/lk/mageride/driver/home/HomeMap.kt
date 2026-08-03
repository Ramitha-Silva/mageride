package lk.mageride.driver.home

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap
import lk.mageride.driver.R
import lk.mageride.driver.location.Fix
import lk.mageride.driver.map.MageRideMap
import lk.mageride.driver.map.MapCamera
import lk.mageride.driver.map.VehicleLayers
import lk.mageride.driver.map.centreOn
import lk.mageride.driver.ui.forType
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.VehicleType
import org.maplibre.android.geometry.LatLng
import org.maplibre.android.maps.MapLibreMap
import org.maplibre.android.maps.Style
import org.maplibre.android.style.layers.PropertyFactory
import org.maplibre.android.style.sources.GeoJsonSource
import org.maplibre.geojson.Feature
import org.maplibre.geojson.FeatureCollection
import org.maplibre.geojson.Point

/**
 * **AL-31 — the driver home map shows the driver's OWN active vehicle and nothing else.**
 *
 * D2' §SCR-DA-010 is explicit: *"other drivers' active vehicles are never shown on the driver home
 * map"*. It is not a filter applied to a wider feed — there is no wider feed to filter.
 * `LiveMapScope.DriverHomeMap` joins **no geocell group at all**, by construction, so the only
 * position this screen can draw is the one the handset's own GNSS produced. A future component that
 * wanted demand heat here would be adding a subscription, not relaxing a filter, and would need a
 * micro-change-set first.
 *
 * The marker is MAP-06's heading arrow tinted by MAP-03's per-type colour, drawn through the
 * shell's own `VehicleLayers` so this vehicle is the same colour here, on My Vehicles and on the
 * passenger map. MAP-02's accuracy circle sits under it.
 *
 * @param position The handset's last fix. `null` before the first one — the map opens on Colombo
 *   Fort and moves as soon as GNSS answers, rather than showing an empty grey rectangle.
 * @param vehicleType The live vehicle's type, for the marker colour; `null` falls back to
 *   `vehPrivate` grey, which is what an unknown type is meant to look like.
 * @param heading Overrides the fix's own bearing for MAP-06's arrow. SCR-DA-013 points it at the
 *   Directional destination rather than at where the vehicle happens to be facing — that is the
 *   *"➤ heading marker"* the wireframe draws on the filter's map preview.
 * @param geofence MAP-10's 100 m circle, at the point the driver is being sent to. SCR-DA-015's
 *   pickup pin; `null` everywhere else.
 */
@Composable
internal fun DriverHomeMap(
    position: Fix?,
    vehicleType: VehicleType?,
    modifier: Modifier = Modifier,
    heading: Double? = null,
    geofence: GeoPoint? = null,
    onRecentre: (() -> Unit)? = null,
) {
    val context = LocalContext.current
    val vehicleColours = MageRideTheme.vehicle
    val markerColour = vehicleType?.let { vehicleColours.forType(it).toArgb() }
        ?: vehicleColours.private.toArgb()
    val clusterColour = MaterialTheme.colorScheme.primaryContainer.toArgb()
    val clusterTextColour = MaterialTheme.colorScheme.onPrimaryContainer.toArgb()
    val accuracyColour = MaterialTheme.colorScheme.primary.toArgb()
    val geofenceColour = MageRideTheme.status.success.toArgb()

    var map by remember { mutableStateOf<MapLibreMap?>(null) }
    var style by remember { mutableStateOf<Style?>(null) }

    MageRideMap(
        modifier = modifier,
        camera = position?.let { MapCamera(lat = it.lat, lng = it.lng) } ?: MapCamera.Default,
        onRecentre = onRecentre ?: {
            val fix = position
            if (fix != null) map?.centreOn(LatLng(fix.lat, fix.lng))
        },
        onMapReady = { ready, readyStyle ->
            // The shell installs no marker asset on purpose — the icon is MAP-03's per-type symbol
            // and belongs to the screen that knows which types are on the map. Here that is one.
            ContextCompat.getDrawable(context, R.drawable.ic_vehicle_marker)?.let { drawable ->
                readyStyle.addImage(MARKER_IMAGE, drawable.toBitmap())
            }
            VehicleLayers.addVehicles(
                style = readyStyle,
                markerImage = MARKER_IMAGE,
                clusterColour = clusterColour,
                clusterTextColour = clusterTextColour,
            )
            VehicleLayers.addCircles(
                style = readyStyle,
                accuracyColour = accuracyColour,
                geofenceColour = geofenceColour,
            )
            readyStyle.getLayer(VehicleLayers.LAYER_VEHICLES)
                ?.setProperties(PropertyFactory.iconColor(markerColour))

            map = ready
            style = readyStyle
        },
    )

    LaunchedEffect(style, position, vehicleType, heading, geofence) {
        val installed = style ?: return@LaunchedEffect
        val fix = position ?: return@LaunchedEffect
        installed.drawOwnVehicle(fix, vehicleType, heading, geofence)
    }
}

/**
 * Replaces the whole vehicle source with the one feature this map is allowed to draw.
 *
 * `setGeoJson` with a single-feature collection rather than an append: the source's contents *are*
 * the driver's current position, and a source that accumulated would leave a trail of the driver's
 * own history on their standby map.
 */
private fun Style.drawOwnVehicle(fix: Fix, vehicleType: VehicleType?, heading: Double?, geofence: GeoPoint?) {
    val source = getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_VEHICLES) ?: return

    val vehicle = Feature.fromGeometry(Point.fromLngLat(fix.lng, fix.lat)).apply {
        addStringProperty(VehicleLayers.PROP_VEHICLE_TYPE, vehicleType?.name?.lowercase().orEmpty())
        addNumberProperty(VehicleLayers.PROP_HEADING, heading ?: fix.headingDeg?.toDouble() ?: 0.0)
    }
    source.setGeoJson(FeatureCollection.fromFeature(vehicle))

    // MAP-02's accuracy circle and MAP-10's geofence share one source and are told apart by
    // `circleKind` — that is how `VehicleLayers.addCircles` filters its two layers.
    getSourceAs<GeoJsonSource>(VehicleLayers.SOURCE_CIRCLES)?.let { circles ->
        val accuracy = Feature.fromGeometry(Point.fromLngLat(fix.lng, fix.lat)).apply {
            addStringProperty(VehicleLayers.PROP_CIRCLE_KIND, VehicleLayers.CIRCLE_ACCURACY)
        }
        val features = listOfNotNull(
            accuracy,
            geofence?.let { target ->
                Feature.fromGeometry(Point.fromLngLat(target.lng, target.lat)).apply {
                    addStringProperty(VehicleLayers.PROP_CIRCLE_KIND, VehicleLayers.CIRCLE_GEOFENCE)
                }
            },
        )
        circles.setGeoJson(FeatureCollection.fromFeatures(features))
    }
}

/** The style image id the single `SymbolLayer` draws. Local to this screen; the shell has none. */
private const val MARKER_IMAGE = "mageride-own-vehicle"
