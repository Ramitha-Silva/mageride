package lk.mageride.driver.ui

import androidx.annotation.StringRes
import lk.mageride.driver.R
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VehicleType

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
