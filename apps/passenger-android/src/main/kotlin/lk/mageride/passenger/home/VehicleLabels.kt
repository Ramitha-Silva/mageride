package lk.mageride.passenger.home

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AirportShuttle
import androidx.compose.material.icons.filled.DirectionsBus
import androidx.compose.material.icons.filled.DirectionsCar
import androidx.compose.material.icons.filled.ElectricRickshaw
import androidx.compose.material.icons.filled.LocalShipping
import androidx.compose.material.icons.filled.Train
import androidx.compose.material.icons.filled.TwoWheeler
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.VehicleColors
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType

/**
 * D2' §0.2's vehicle table, as a screen needs it: a name, an icon and a colour per type.
 *
 * The colour is the **same** `VehicleColors.Legend` entry the map marker uses, which is the whole
 * point of SCR-PA-006's own state line: *"each type shows its small vehicle icon tinted with the
 * canonical AL-09 colour (not a plain dot) so the chip matches the map marker colour"*. A second
 * table here would be the second copy of §0.2 that MAP-03 exists to prevent.
 *
 * The icons are §0.2's own Compose Material Symbol names, verbatim from the table — `directions_bus`,
 * `train` (the *distinct rail icon* §0.2 calls out), `two_wheeler`, `electric_rickshaw`,
 * `directions_car`, `airport_shuttle`, `local_shipping`.
 */
internal object VehicleLabels {

    /** The type's display name. Trilingual, unlike the wire spelling. */
    @StringRes
    fun name(type: VehicleType): Int = when (type) {
        VehicleType.BUS -> R.string.vehicle_bus
        VehicleType.TRAIN -> R.string.vehicle_train
        VehicleType.MOTORBIKE -> R.string.vehicle_motorbike
        VehicleType.THREE_WHEELER -> R.string.vehicle_three_wheeler
        VehicleType.FLEX -> R.string.vehicle_flex
        VehicleType.SEDAN -> R.string.vehicle_sedan
        VehicleType.MINI_VAN -> R.string.vehicle_mini_van
        VehicleType.VAN -> R.string.vehicle_van
        VehicleType.TRUCK -> R.string.vehicle_truck
        VehicleType.MINI_TRUCK -> R.string.vehicle_mini_truck
    }

    /** §0.2's icon column. */
    fun icon(type: VehicleType): ImageVector = when (type) {
        VehicleType.BUS -> Icons.Filled.DirectionsBus

        // §0.2: "rail icon, distinct" — a train sharing the bus glyph is the one confusion Mode A
        // cannot afford, and it is called out in the spec's own table.
        VehicleType.TRAIN -> Icons.Filled.Train

        VehicleType.MOTORBIKE -> Icons.Filled.TwoWheeler

        VehicleType.THREE_WHEELER -> Icons.Filled.ElectricRickshaw

        VehicleType.FLEX, VehicleType.SEDAN -> Icons.Filled.DirectionsCar

        VehicleType.MINI_VAN, VehicleType.VAN -> Icons.Filled.AirportShuttle

        VehicleType.TRUCK, VehicleType.MINI_TRUCK -> Icons.Filled.LocalShipping
    }

    /** MAP-03's marker colour for the type — the same value the `SymbolLayer` tints with. */
    fun colour(legend: VehicleColors, type: VehicleType): Color = when (type) {
        VehicleType.BUS -> legend.bus
        VehicleType.TRAIN -> legend.train
        VehicleType.MOTORBIKE -> legend.motorbike
        VehicleType.THREE_WHEELER -> legend.threeWheeler
        VehicleType.FLEX -> legend.flex
        VehicleType.SEDAN -> legend.sedan
        VehicleType.MINI_VAN -> legend.miniVan
        VehicleType.VAN -> legend.van
        VehicleType.TRUCK -> legend.truck
        VehicleType.MINI_TRUCK -> legend.miniTruck
    }

    /** The mode row's label — the wireframe's *"Mode A — Bus & Train"*. */
    @StringRes
    fun modeName(mode: ServiceMode): Int = when (mode) {
        ServiceMode.A -> R.string.mode_a
        ServiceMode.B -> R.string.mode_b
        ServiceMode.C -> R.string.mode_c
    }

    /**
     * The mode's badge letter.
     *
     * `A`/`B`/`C` are the same character in all three scripts, so this is a Kotlin constant rather
     * than three identical `strings.xml` values — which `StringResourceTest` reads as a translation
     * nobody did.
     */
    fun modeBadge(mode: ServiceMode): String = mode.name
}
