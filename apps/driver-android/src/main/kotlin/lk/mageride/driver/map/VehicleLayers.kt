package lk.mageride.driver.map

import lk.mageride.shared.data.models.VehicleType
import org.maplibre.android.maps.Style
import org.maplibre.android.style.expressions.Expression
import org.maplibre.android.style.layers.CircleLayer
import org.maplibre.android.style.layers.PropertyFactory
import org.maplibre.android.style.layers.SymbolLayer
import org.maplibre.android.style.sources.GeoJsonOptions
import org.maplibre.android.style.sources.GeoJsonSource

/**
 * The MAP-* primitives D2' §0.3 makes a hard rule, installed onto a style.
 *
 * > *"MapLibre GL Native style (light/dark PMTiles), `SymbolLayer` markers by `vehVeh*` color +
 * > heading arrow (MAP-06), `ClusterLayer` when zoomed out (MAP-05), interpolated marker animation
 * > (MAP-04), `LineLayer` trip polyline (MAP-08), accuracy circle (MAP-02), 100m geofence circle
 * > (MAP-10)."*
 *
 * These are the pieces more than one screen needs — the driver dashboard and the passenger live
 * map draw the same vehicle the same colour, and MAP-03's legend only means anything if they do.
 * What each screen *puts* in the sources is its own business; this owns the sources and layers,
 * not their contents.
 *
 * **Colours come from the theme's [lk.mageride.driver.ui.theme.VehicleColors], via
 * [markerColourExpression].** A hex written here would be the second copy of §0.2's table.
 */
internal object VehicleLayers {

    /** Source id for live vehicle positions. Features carry `vehicleType` and `heading`. */
    const val SOURCE_VEHICLES: String = "mageride-vehicles"

    /** Source id for the accuracy circle (MAP-02) and the 100 m geofence (MAP-10). */
    const val SOURCE_CIRCLES: String = "mageride-circles"

    const val LAYER_VEHICLES: String = "mageride-vehicles-symbols"
    const val LAYER_CLUSTERS: String = "mageride-vehicles-clusters"
    const val LAYER_CLUSTER_COUNT: String = "mageride-vehicles-cluster-count"
    const val LAYER_ACCURACY: String = "mageride-accuracy"
    const val LAYER_GEOFENCE: String = "mageride-geofence"

    /** Feature property carrying the canonical vehicle type (AL-09), so MAP-03 can colour it. */
    const val PROP_VEHICLE_TYPE: String = "vehicleType"

    /** Feature property carrying the heading in degrees — MAP-06's arrow rotation. */
    const val PROP_HEADING: String = "heading"

    /** Feature property distinguishing the accuracy circle from the geofence one. */
    const val PROP_CIRCLE_KIND: String = "circleKind"

    const val CIRCLE_ACCURACY: String = "accuracy"
    const val CIRCLE_GEOFENCE: String = "geofence"

    /** D5' §7's arrival geofence, and the radius MAP-10 draws. */
    const val GEOFENCE_RADIUS_M: Int = 100

    /**
     * Adds the vehicle source and its three layers to [style].
     *
     * **Clustering is on the source, not the layer** — that is how MapLibre expresses MAP-05, and
     * why `LAYER_CLUSTERS` filters on `point_count` while `LAYER_VEHICLES` filters on its absence.
     * Without the paired filters a clustered point renders twice: once as its cluster and once as
     * itself.
     *
     * @param markerImage The image id already added to the style with `style.addImage(...)`. The
     *   shell does not ship a marker asset — the icon is MAP-03's per-type symbol and belongs to
     *   the screen that knows which types are on the map.
     * @param clusterColour The `primaryContainer` of the theme in force.
     * @param clusterTextColour Its `onPrimaryContainer`.
     */
    fun addVehicles(style: Style, markerImage: String, clusterColour: Int, clusterTextColour: Int) {
        style.addSource(
            GeoJsonSource(
                SOURCE_VEHICLES,
                GeoJsonOptions()
                    .withCluster(true)
                    // Below zoom 14 a city's worth of taxis is an unreadable smear of pins; above
                    // it a driver is looking at individual vehicles near a junction.
                    .withClusterMaxZoom(CLUSTER_MAX_ZOOM)
                    .withClusterRadius(CLUSTER_RADIUS_PX),
            ),
        )

        style.addLayer(
            SymbolLayer(LAYER_VEHICLES, SOURCE_VEHICLES)
                .withProperties(
                    PropertyFactory.iconImage(markerImage),
                    // MAP-06. `icon-rotate` off the feature's own heading, and
                    // `icon-rotation-alignment: map` so the arrow points where the vehicle is
                    // going rather than where the screen is turned.
                    PropertyFactory.iconRotate(Expression.get(PROP_HEADING)),
                    PropertyFactory.iconRotationAlignment("map"),
                    PropertyFactory.iconAllowOverlap(true),
                    PropertyFactory.iconIgnorePlacement(true),
                )
                .withFilter(Expression.not(Expression.has("point_count"))),
        )

        style.addLayer(
            CircleLayer(LAYER_CLUSTERS, SOURCE_VEHICLES)
                .withProperties(
                    PropertyFactory.circleColor(clusterColour),
                    PropertyFactory.circleRadius(
                        Expression.step(
                            Expression.get("point_count"),
                            Expression.literal(CLUSTER_RADIUS_SMALL),
                            Expression.stop(CLUSTER_STEP_MEDIUM, CLUSTER_RADIUS_MEDIUM),
                            Expression.stop(CLUSTER_STEP_LARGE, CLUSTER_RADIUS_LARGE),
                        ),
                    ),
                )
                .withFilter(Expression.has("point_count")),
        )

        style.addLayer(
            SymbolLayer(LAYER_CLUSTER_COUNT, SOURCE_VEHICLES)
                .withProperties(
                    PropertyFactory.textField(Expression.toString(Expression.get("point_count"))),
                    PropertyFactory.textSize(CLUSTER_TEXT_SIZE),
                    PropertyFactory.textColor(clusterTextColour),
                    PropertyFactory.textIgnorePlacement(true),
                    PropertyFactory.textAllowOverlap(true),
                )
                .withFilter(Expression.has("point_count")),
        )
    }

    /**
     * Adds MAP-02's accuracy circle and MAP-10's 100 m geofence circle.
     *
     * One source and two layers rather than two sources: both are circles around a point and
     * differ only in radius and paint, and a feature's [PROP_CIRCLE_KIND] is what selects between
     * them. The radii are in metres, which is what `circleRadius` needs converting for — the
     * caller passes pixels computed from the current zoom, because MapLibre's circle radius is a
     * screen measurement and a metre is not.
     */
    fun addCircles(style: Style, accuracyColour: Int, geofenceColour: Int) {
        style.addSource(GeoJsonSource(SOURCE_CIRCLES))

        style.addLayerBelow(
            CircleLayer(LAYER_ACCURACY, SOURCE_CIRCLES)
                .withProperties(
                    PropertyFactory.circleColor(accuracyColour),
                    PropertyFactory.circleOpacity(ACCURACY_OPACITY),
                    PropertyFactory.circleStrokeWidth(0f),
                )
                .withFilter(Expression.eq(Expression.get(PROP_CIRCLE_KIND), CIRCLE_ACCURACY)),
            LAYER_VEHICLES,
        )

        style.addLayerBelow(
            CircleLayer(LAYER_GEOFENCE, SOURCE_CIRCLES)
                .withProperties(
                    PropertyFactory.circleColor(geofenceColour),
                    PropertyFactory.circleOpacity(GEOFENCE_OPACITY),
                    PropertyFactory.circleStrokeColor(geofenceColour),
                    PropertyFactory.circleStrokeWidth(GEOFENCE_STROKE_PX),
                )
                .withFilter(Expression.eq(Expression.get(PROP_CIRCLE_KIND), CIRCLE_GEOFENCE)),
            LAYER_VEHICLES,
        )
    }

    /**
     * MAP-03's legend as a MapLibre `match` expression over [PROP_VEHICLE_TYPE].
     *
     * Built from the caller's colours rather than from constants here, so the one place a vehicle
     * colour is written stays `ui/theme/Color.kt`. The fallback is `vehPrivate` grey — a vehicle
     * type this build does not know is still drawn, because a missing marker reads as a platform
     * outage and an unfamiliar grey one reads as what it is.
     *
     * @param colours Wire name → ARGB int, from `VehicleColors.Legend`.
     * @param fallback `vehPrivate`.
     */
    // MapLibre's `Expression.match` is vararg-only, so the stops have to be spread. The copy is
    // eleven elements, built once when a style is installed rather than per frame.
    @Suppress("SpreadOperator")
    fun markerColourExpression(colours: Map<String, Int>, fallback: Int): Expression {
        val stops = colours.flatMap { (type, colour) ->
            listOf(Expression.literal(type), Expression.color(colour))
        }.toTypedArray()
        return Expression.match(Expression.get(PROP_VEHICLE_TYPE), Expression.color(fallback), *stops)
    }

    /** The wire spelling of every canonical vehicle type, for building the colour map. */
    fun vehicleTypeNames(): List<String> = VehicleType.entries.map { it.name.lowercase() }

    private const val CLUSTER_MAX_ZOOM = 14
    private const val CLUSTER_RADIUS_PX = 60
    private const val CLUSTER_RADIUS_SMALL = 16f
    private const val CLUSTER_RADIUS_MEDIUM = 22f
    private const val CLUSTER_RADIUS_LARGE = 28f
    private const val CLUSTER_STEP_MEDIUM = 10
    private const val CLUSTER_STEP_LARGE = 40
    private const val CLUSTER_TEXT_SIZE = 12f
    private const val ACCURACY_OPACITY = 0.15f
    private const val GEOFENCE_OPACITY = 0.08f
    private const val GEOFENCE_STROKE_PX = 2f
}
