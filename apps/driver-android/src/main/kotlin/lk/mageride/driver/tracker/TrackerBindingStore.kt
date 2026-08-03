package lk.mageride.driver.tracker

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import lk.mageride.shared.data.models.Ulid

/**
 * One tracker bound to one vehicle, as this handset knows it.
 *
 * @property vehicleId The vehicle the device publishes for.
 * @property imei The 15-digit IMEI that was bound.
 * @property bindingId The `prov.tracker_bindings` row `POST /v1/vehicles/{id}/device` answered with.
 *   Its existence **is** the credential status: provisioning-svc mints the X.509 or the signed PSK
 *   inside that call (T-02), so a `201` is D2' §SCR-DA-027's *"paired → cert-issued confirmation"*.
 */
internal data class TrackerBinding(val vehicleId: Ulid, val imei: String, val bindingId: Ulid)

/**
 * Which of this driver's vehicles have a hardware tracker on them.
 *
 * **This is local, and it has to be** — not by preference, the way `ActiveVehicleStore` is, but
 * because nothing on the app-facing surface answers the question. `POST /v1/vehicles/{id}/device`
 * is the only tracker route `:shared` has; it returns a `bindingId` and nothing reads one back.
 * `VehicleSummary` and `VehicleDetail` carry no device field, `GET /v1/trackers/{imei}` is
 * provisioning-svc's and has no client here, and there is no "which vehicles of mine are tracked"
 * read anywhere in `registry.yaml`. Recorded as the first C074 spec gap.
 *
 * What hangs off it is not cosmetic: [TrackerPositionPublisher] refuses to start the phone's
 * publisher for a vehicle in this store, which is US-3.6's *"exactly one publisher at a time"*. A
 * driver who pairs on one handset and drives with another therefore still publishes from the
 * second — the honest limit of a device-local answer, and the reason the gap is worth closing
 * server-side rather than working around here.
 *
 * An interface with an Android implementation for the same reason `OnboardingPreferences` is one:
 * a local unit test's `SharedPreferences` is a stub whose every member answers a default.
 */
internal interface TrackerBindingStore {

    /** The tracker bound to [vehicleId], or `null` when this handset has not paired one. */
    fun bindingFor(vehicleId: Ulid): TrackerBinding?

    /** Records a successful pair. Replaces any earlier binding on the same vehicle. */
    fun remember(binding: TrackerBinding)
}

/** [TrackerBindingStore] over a private `SharedPreferences` file. An IMEI is not a secret. */
internal class AndroidTrackerBindingStore(context: Context) : TrackerBindingStore {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override fun bindingFor(vehicleId: Ulid): TrackerBinding? =
        store.getString(vehicleId, null)?.split(SEPARATOR)?.takeIf { it.size == FIELDS }?.let { parts ->
            TrackerBinding(vehicleId = vehicleId, imei = parts[0], bindingId = parts[1])
        }

    override fun remember(binding: TrackerBinding) {
        store.edit { putString(binding.vehicleId, "${binding.imei}$SEPARATOR${binding.bindingId}") }
    }

    private companion object {
        const val FILE = "driver_trackers"

        /** Neither a ULID nor an IMEI can contain it, so the split is unambiguous. */
        const val SEPARATOR = "|"
        const val FIELDS = 2
    }
}
