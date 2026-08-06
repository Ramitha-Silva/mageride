package lk.mageride.passenger.map

import lk.mageride.shared.data.models.VehicleType
import org.maplibre.android.maps.Style
import org.maplibre.android.style.expressions.Expression
import org.maplibre.android.style.layers.CircleLayer
import org.maplibre.android.style.layers.LineLayer
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
 * > (MAP-10). Pins: `pickup` green, `dropoff` red, `user` blue dot."*
 *
 * Everything on that list except MAP-04 is a layer, and every one of them is here; MAP-04 is
 * [MarkerInterpolator], because a tween is arithmetic rather than cartography and MapLibre does
 * not tween a source's contents. MAP-09 — offline tile caching — is the one item of the ten this
 * component does not carry; see [MapStyles].
 *
 * This owns the sources and the layers, not their contents: what goes *in* a source is
 * [MageRideMap]'s and, above it, the screen's.
 *
 * **Colours are passed in, never written here.** They come from the theme's
 * [lk.mageride.passenger.ui.theme.VehicleColors] through [markerColourExpression]; a hex in this
 * file would be the second copy of §0.2's table.
 */
internal object VehicleLayers {

    /** Source id for live vehicle positions. Features carry [PROP_VEHICLE_TYPE] and [PROP_HEADING]. */
    const val SOURCE_VEHICLES: String = "mageride-vehicles"

    /** Source id for MAP-02's accuracy circle and MAP-10's 100 m geofence. */
    const val SOURCE_CIRCLES: String = "mageride-circles"

    /** Source id for MAP-08's trip polyline. */
    const val SOURCE_ROUTE: String = "mageride-route"

    /** Source id for §0.3's pickup / dropoff / user pins. */
    const val SOURCE_PINS: String = "mageride-pins"

    const val LAYER_VEHICLES: String = "mageride-vehicles-symbols"
    const val LAYER_CLUSTERS: String = "mageride-vehicles-clusters"
    const val LAYER_CLUSTER_COUNT: String = "mageride-vehicles-cluster-count"
    const val LAYER_ACCURACY: String = "mageride-accuracy"
    const val LAYER_GEOFENCE: String = "mageride-geofence"
    const val LAYER_ROUTE_CASING: String = "mageride-route-casing"
    const val LAYER_ROUTE: String = "mageride-route-line"
    const val LAYER_PINS: String = "mageride-pins-circles"

    /** Feature property carrying the canonical vehicle type (AL-09), so MAP-03 can colour it. */
    const val PROP_VEHICLE_TYPE: String = "vehicleType"

    /** Feature property carrying the heading in degrees — MAP-06's arrow rotation. */
    const val PROP_HEADING: String = "heading"

    /** Feature property carrying the vehicle id, so MAP-07's tap knows what was tapped. */
    const val PROP_VEHICLE_ID: String = "vehicleId"

    /** Feature property distinguishing the accuracy circle from the geofence one. */
    const val PROP_CIRCLE_KIND: String = "circleKind"

    /** Feature property selecting a pin's colour — one of [PIN_PICKUP], [PIN_DROPOFF], [PIN_USER]. */
    const val PROP_PIN_KIND: String = "pinKind"

    const val CIRCLE_ACCURACY: String = "accuracy"
    const val CIRCLE_GEOFENCE: String = "geofence"

    const val PIN_PICKUP: String = "pickup"
    const val PIN_DROPOFF: String = "dropoff"
    const val PIN_USER: String = "user"

    /** D5' §7's arrival geofence, and the radius MAP-10 draws. */
    const val GEOFENCE_RADIUS_M: Int = 100

    /**
     * Installs every layer, **in draw order**, on a style that has just loaded.
     *
     * The order is the argument for having one function rather than four: MapLibre stacks a layer
     * on top of whatever is already there, so circles first (they are context, and a vehicle drawn
     * under its own accuracy halo disappears), then the trip polyline, then the vehicles, then the
     * pins that mark where the passenger is going. A caller that added them itself would get the
     * order right the first time and wrong the second.
     *
     * @param markerImage The image id already added to the style with `style.addImage(...)` —
     *   MAP-06's heading arrow. [MageRideMap] adds it; nothing here ships an asset.
     * @param palette Every colour, resolved from D2' §0.2 by [mapPalette]. Passed in rather than
     *   read here so this file stays free of Compose and free of hexes.
     */
    fun install(style: Style, markerImage: String, palette: MapPalette) {
        addCircles(style, palette.circles)
        addRoute(style, palette.route)
        addVehicles(style, markerImage, palette.vehicles)
        addPins(style, palette.pins)
    }

    /**
     * MAP-03 / MAP-05 / MAP-06 — the vehicle source and its three layers.
     *
     * **Clustering is on the source, not the layer** — that is how MapLibre expresses MAP-05, and
     * why [LAYER_CLUSTERS] filters on `point_count` while [LAYER_VEHICLES] filters on its absence.
     * Without the paired filters a clustered point renders twice: once as its cluster and once as
     * itself.
     */
    private fun addVehicles(style: Style, markerImage: String, palette: VehiclePalette) {
        style.addSource(
            GeoJsonSource(
                SOURCE_VEHICLES,
                GeoJsonOptions()
                    .withCluster(true)
                    // Below zoom 14 a city's worth of vehicles is an unreadable smear of pins;
                    // above it a passenger is looking at individual vehicles near a junction.
                    .withClusterMaxZoom(CLUSTER_MAX_ZOOM)
                    .withClusterRadius(CLUSTER_RADIUS_PX),
            ),
        )

        style.addLayer(
            SymbolLayer(LAYER_VEHICLES, SOURCE_VEHICLES)
                .withProperties(
                    PropertyFactory.iconImage(markerImage),
                    // MAP-03. The whole eleven-colour legend as one `match` — this map draws every
                    // mode in a cell at once, so a single `icon-color` (which is what the driver's
                    // own-vehicle map uses) cannot express it.
                    PropertyFactory.iconColor(markerColourExpression(palette.legend, palette.fallback)),
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
                    PropertyFactory.circleColor(palette.cluster),
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
                    PropertyFactory.textColor(palette.onCluster),
                    PropertyFactory.textIgnorePlacement(true),
                    PropertyFactory.textAllowOverlap(true),
                )
                .withFilter(Expression.has("point_count")),
        )
    }

    /**
     * MAP-02's accuracy circle and MAP-10's 100 m geofence.
     *
     * One source and two layers rather than two sources: both are circles around a point and
     * differ only in radius and paint, and a feature's [PROP_CIRCLE_KIND] is what selects between
     * them. The radii are in metres, which is what `circleRadius` needs converting for — the
     * caller passes pixels computed from the current zoom, because MapLibre's circle radius is a
     * screen measurement and a metre is not.
     */
    private fun addCircles(style: Style, palette: CirclePalette) {
        style.addSource(GeoJsonSource(SOURCE_CIRCLES))

        style.addLayer(
            CircleLayer(LAYER_ACCURACY, SOURCE_CIRCLES)
                .withProperties(
                    PropertyFactory.circleColor(palette.accuracy),
                    PropertyFactory.circleOpacity(ACCURACY_OPACITY),
                    PropertyFactory.circleStrokeWidth(0f),
                )
                .withFilter(Expression.eq(Expression.get(PROP_CIRCLE_KIND), CIRCLE_ACCURACY)),
        )

        style.addLayer(
            CircleLayer(LAYER_GEOFENCE, SOURCE_CIRCLES)
                .withProperties(
                    PropertyFactory.circleColor(palette.geofence),
                    PropertyFactory.circleOpacity(GEOFENCE_OPACITY),
                    PropertyFactory.circleStrokeColor(palette.geofence),
                    PropertyFactory.circleStrokeWidth(GEOFENCE_STROKE_PX),
                )
                .withFilter(Expression.eq(Expression.get(PROP_CIRCLE_KIND), CIRCLE_GEOFENCE)),
        )
    }

    /**
     * MAP-08 — the trip polyline, as two `LineLayer`s.
     *
     * A casing under the line rather than one stroke: the basemap's own roads are white on grey
     * in the light style, so a single orange line at four pixels reads as a road on a screen in
     * sunlight. The casing is the same geometry, wider and darker, which is how every navigation
     * map draws a route and is the cheapest way to keep it legible over both styles.
     */
    private fun addRoute(style: Style, palette: RoutePalette) {
        style.addSource(GeoJsonSource(SOURCE_ROUTE))

        style.addLayer(
            LineLayer(LAYER_ROUTE_CASING, SOURCE_ROUTE).withProperties(
                PropertyFactory.lineColor(palette.casing),
                PropertyFactory.lineWidth(ROUTE_CASING_WIDTH_PX),
                PropertyFactory.lineCap("round"),
                PropertyFactory.lineJoin("round"),
            ),
        )

        style.addLayer(
            LineLayer(LAYER_ROUTE, SOURCE_ROUTE).withProperties(
                PropertyFactory.lineColor(palette.line),
                PropertyFactory.lineWidth(ROUTE_WIDTH_PX),
                PropertyFactory.lineCap("round"),
                PropertyFactory.lineJoin("round"),
            ),
        )
    }

    /**
     * §0.3's *"`pickup` green, `dropoff` red, `user` blue dot"*.
     *
     * Circles rather than teardrop symbols, because a circle needs no asset and the spec's own
     * word for the third one is "dot". A screen that wants a labelled teardrop for a pickup adds
     * its own `SymbolLayer` above this one; what must not vary between screens is the *colour*,
     * which is why the three are one table passed in from the theme.
     */
    private fun addPins(style: Style, palette: PinPalette) {
        style.addSource(GeoJsonSource(SOURCE_PINS))

        val stops = palette.byKind.flatMap { (kind, colour) ->
            listOf(Expression.literal(kind), Expression.color(colour))
        }.toTypedArray()

        style.addLayer(
            CircleLayer(LAYER_PINS, SOURCE_PINS).withProperties(
                PropertyFactory.circleColor(
                    // Three stops, one per pin kind. `Expression.match` is vararg-only, hence
                    // the spread; the fallback is the user dot's own colour, because an unknown
                    // pin kind is a client bug and drawing nothing would hide it.
                    @Suppress("SpreadOperator")
                    Expression.match(
                        Expression.get(PROP_PIN_KIND),
                        Expression.color(palette.byKind[PIN_USER] ?: palette.stroke),
                        *stops,
                    ),
                ),
                PropertyFactory.circleRadius(PIN_RADIUS_PX),
                PropertyFactory.circleStrokeColor(palette.stroke),
                PropertyFactory.circleStrokeWidth(PIN_STROKE_PX),
            ),
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
    fun vehicleTypeNames(): List<String> = VehicleType.entries.map(VehicleType::wire)

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
    private const val ROUTE_WIDTH_PX = 5f
    private const val ROUTE_CASING_WIDTH_PX = 9f
    private const val PIN_RADIUS_PX = 7f
    private const val PIN_STROKE_PX = 2f
}
