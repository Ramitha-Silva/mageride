package lk.mageride.passenger.map

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.graphics.toArgb
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.passenger.ui.theme.PinColors
import lk.mageride.shared.data.models.VehicleType

/**
 * Every colour D2' §0.3's layer stack needs, resolved from the theme once.
 *
 * MapLibre wants ARGB `Int`s and knows nothing about `MaterialTheme`, so the two have to meet
 * somewhere. Meeting *here* rather than at each `addLayer` call is what keeps `VehicleLayers` free
 * of Compose — it is style plumbing, and a style is installed from a callback that has no
 * composition around it.
 *
 * Grouped rather than flat because the groups are real: MAP-03's legend and MAP-05's clusters are
 * one layer family, MAP-02 and MAP-10 are another, MAP-08 is a third, §0.3's pins a fourth.
 *
 * @property vehicles MAP-03 / MAP-05 / MAP-06.
 * @property circles MAP-02's accuracy halo and MAP-10's 100 m geofence.
 * @property route MAP-08's trip polyline.
 * @property pins §0.3's `pickup` green, `dropoff` red and `user` blue dot.
 */
internal data class MapPalette(
    val vehicles: VehiclePalette,
    val circles: CirclePalette,
    val route: RoutePalette,
    val pins: PinPalette,
)

/**
 * @property legend MAP-03's eleven colours, keyed by the **wire** spelling MapLibre's `match`
 *   expression compares `vehicleType` against — `three_wheeler`, not `THREE_WHEELER`.
 * @property fallback `vehPrivate` grey, for a type this build does not know. A missing marker
 *   reads as a platform outage; an unfamiliar grey one reads as what it is.
 * @property cluster MAP-05's disc — the theme's `primaryContainer`.
 * @property onCluster The count printed on it.
 */
internal data class VehiclePalette(
    val legend: Map<String, Int>,
    val fallback: Int,
    val cluster: Int,
    val onCluster: Int,
)

/**
 * @property accuracy MAP-02's halo — the theme's `primary`, at a low opacity.
 * @property geofence MAP-10's 100 m circle — the theme's `success`, because arriving is good news.
 */
internal data class CirclePalette(val accuracy: Int, val geofence: Int)

/**
 * @property line MAP-08's polyline — the theme's `primary`.
 * @property casing The wider, darker stroke underneath. The light basemap draws roads white on
 *   grey, so a single orange line reads as a road in sunlight; a casing is how every navigation
 *   map keeps a route legible over both styles.
 * @property walk SCR-PA-009's **blue, dashed** walk-to-halt leg. Deliberately the §0.3 user-dot
 *   blue rather than the route colour: that leg is the passenger's, not the bus's. Δ C079.
 */
internal data class RoutePalette(val line: Int, val casing: Int, val walk: Int)

/**
 * @property byKind `VehicleLayers.PIN_PICKUP` / `PIN_DROPOFF` / `PIN_USER` → ARGB.
 * @property stroke The ring around a pin, so it reads on any basemap colour.
 */
internal data class PinPalette(val byKind: Map<String, Int>, val stroke: Int)

/** The palette for the theme in force. The one place §0.2's tokens become MapLibre's integers. */
internal val mapPalette: MapPalette
    @Composable @ReadOnlyComposable
    get() {
        val legend = MageRideTheme.vehicle
        return MapPalette(
            vehicles = VehiclePalette(
                legend = mapOf(
                    VehicleType.BUS.wire to legend.bus.toArgb(),
                    VehicleType.TRAIN.wire to legend.train.toArgb(),
                    VehicleType.MOTORBIKE.wire to legend.motorbike.toArgb(),
                    VehicleType.THREE_WHEELER.wire to legend.threeWheeler.toArgb(),
                    VehicleType.FLEX.wire to legend.flex.toArgb(),
                    VehicleType.SEDAN.wire to legend.sedan.toArgb(),
                    VehicleType.MINI_VAN.wire to legend.miniVan.toArgb(),
                    VehicleType.VAN.wire to legend.van.toArgb(),
                    VehicleType.TRUCK.wire to legend.truck.toArgb(),
                    VehicleType.MINI_TRUCK.wire to legend.miniTruck.toArgb(),
                ),
                fallback = legend.private.toArgb(),
                cluster = MaterialTheme.colorScheme.primaryContainer.toArgb(),
                onCluster = MaterialTheme.colorScheme.onPrimaryContainer.toArgb(),
            ),
            circles = CirclePalette(
                accuracy = MaterialTheme.colorScheme.primary.toArgb(),
                geofence = MageRideTheme.status.success.toArgb(),
            ),
            route = RoutePalette(
                line = MaterialTheme.colorScheme.primary.toArgb(),
                casing = MaterialTheme.colorScheme.onSurface.toArgb(),
                walk = PinColors.User.toArgb(),
            ),
            pins = PinPalette(
                byKind = mapOf(
                    VehicleLayers.PIN_PICKUP to PinColors.Pickup.toArgb(),
                    VehicleLayers.PIN_DROPOFF to PinColors.Dropoff.toArgb(),
                    VehicleLayers.PIN_USER to PinColors.User.toArgb(),
                ),
                stroke = MaterialTheme.colorScheme.onPrimary.toArgb(),
            ),
        )
    }
