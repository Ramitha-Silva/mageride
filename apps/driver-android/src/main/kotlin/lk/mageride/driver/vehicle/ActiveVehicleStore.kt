package lk.mageride.driver.vehicle

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import lk.mageride.shared.data.models.Ulid

/**
 * Which vehicle this handset publishes as — **D-03's single active publisher**, and SCR-DA-026's
 * *"Only one vehicle can be live at a time"*.
 *
 * **This is deliberately local.** `backend/contracts/registry.yaml` has no "set my active vehicle"
 * operation, and it does not need one: the choice is a property of *this device*, not of the
 * driver. It becomes visible to the platform when the driver goes online — the MQTT username **is**
 * the vehicle id (D6' §3) — so a driver with two approved vehicles picks one here and the broker
 * learns which on CONNECT. A server-side field would be a second answer to a question the
 * connection already answers, and the two would disagree the moment the driver switched handsets.
 *
 * C070's dashboard reads this for the *"Live: 3W · ABC-1234"* chip and for the US-9.6 go-online
 * gate; C069 is what writes it.
 *
 * An interface with an Android implementation for the same reason as `OnboardingPreferences`: a
 * local unit test's `SharedPreferences` is a stub whose every member answers a default.
 */
internal interface ActiveVehicleStore {

    /** The vehicle selected to go live, or `null` when the driver has not chosen one. */
    var activeVehicleId: Ulid?
}

/** [ActiveVehicleStore] over a private `SharedPreferences` file. Nothing here is a secret. */
internal class AndroidActiveVehicleStore(context: Context) : ActiveVehicleStore {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override var activeVehicleId: Ulid?
        get() = store.getString(KEY_ACTIVE_VEHICLE, null)
        set(value) = store.edit { putString(KEY_ACTIVE_VEHICLE, value) }

    private companion object {
        const val FILE = "driver_vehicles"
        const val KEY_ACTIVE_VEHICLE = "active_vehicle_id"
    }
}
