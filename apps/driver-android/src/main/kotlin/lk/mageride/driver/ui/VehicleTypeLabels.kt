package lk.mageride.driver.ui

import androidx.annotation.StringRes
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.theme.VehicleColors
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.VehicleSummary

/**
 * `Three-wheeler · ABC-1234` — how every per-vehicle control in the app names a vehicle.
 *
 * One function since C074, because three screens now draw it: SCR-DA-010's dashboard chip,
 * SCR-DA-027's pairing selector and SCR-DA-028's sharing selector. The type is trilingual and the
 * plate is not — a registration number is a proper noun (C068's rule).
 */
@Composable
internal fun vehicleLabel(vehicle: VehicleSummary): String =
    "${stringResource(vehicle.vehicleType.labelRes())} ${Symbols.DOT} ${vehicle.registrationNumber}"

/**
 * The trilingual display name for a canonical vehicle type (AL-09).
 *
 * One mapping for the whole app. The enum's `wire` value is a machine key —
 * `three_wheeler` is what `registry.vehicles.vehicle_type` stores and what the CHECK constraint
 * enforces — and a screen that title-cased it would show a Sinhala driver an English word.
 */
@StringRes
internal fun VehicleType.labelRes(): Int = when (this) {
    VehicleType.MOTORBIKE -> R.string.vehicle_type_motorbike
    VehicleType.THREE_WHEELER -> R.string.vehicle_type_three_wheeler
    VehicleType.FLEX -> R.string.vehicle_type_flex
    VehicleType.SEDAN -> R.string.vehicle_type_sedan
    VehicleType.MINI_VAN -> R.string.vehicle_type_mini_van
    VehicleType.VAN -> R.string.vehicle_type_van
    VehicleType.TRUCK -> R.string.vehicle_type_truck
    VehicleType.MINI_TRUCK -> R.string.vehicle_type_mini_truck
    VehicleType.BUS -> R.string.vehicle_type_bus
    VehicleType.TRAIN -> R.string.vehicle_type_train
}

/** The same, for the Mode-C subset a driver app can offer (`bus` and `train` are Fleet Portal). */
@StringRes
internal fun RideVehicleType.labelRes(): Int = toVehicleType().labelRes()

/**
 * MAP-03's legend colour for a vehicle type (D2' §0.2).
 *
 * The same eleven hexes the map markers use, so the dot beside `Three-wheeler · ABC-1234` on My
 * Vehicles (SCR-DA-026) is the colour that vehicle is on the map. The wireframe draws it as
 * `--vehTuk`, `--vehSedan`, `--vehVan`; this is that table.
 */
internal fun VehicleColors.forType(type: VehicleType): Color = when (type) {
    VehicleType.MOTORBIKE -> motorbike
    VehicleType.THREE_WHEELER -> threeWheeler
    VehicleType.FLEX -> flex
    VehicleType.SEDAN -> sedan
    VehicleType.MINI_VAN -> miniVan
    VehicleType.VAN -> van
    VehicleType.TRUCK -> truck
    VehicleType.MINI_TRUCK -> miniTruck
    VehicleType.BUS -> bus
    VehicleType.TRAIN -> train
}
