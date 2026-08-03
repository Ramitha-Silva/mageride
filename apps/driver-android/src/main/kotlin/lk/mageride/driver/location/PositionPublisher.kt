package lk.mageride.driver.location

import android.content.Context
import lk.mageride.shared.data.models.Ulid

/**
 * Turning the position publisher on and off, without a `Context` in a view model.
 *
 * Going online is two things that must happen together: `POST /v1/standby/online` puts the driver
 * in the candidate pool, and [PositionForegroundService] starts publishing on
 * `veh/{vehicleId}/pos/live`. A driver in the pool who is not publishing is a driver dispatch
 * offers rides to and cannot find — so the dashboard owns both, and this is the seam that lets it
 * without holding an Android type.
 *
 * The vehicle id is the argument because **the MQTT username is the vehicle id** (D6' §3): the
 * broker learns which of a driver's vehicles is live at CONNECT, which is also why
 * `ActiveVehicleStore` is local.
 */
internal interface PositionPublisher {

    /** Publishes for [vehicleId]. Idempotent — a second call for the same vehicle is a no-op. */
    fun start(vehicleId: Ulid)

    /** Stops publishing and announces `offline` on `veh/{vehicleId}/status`. */
    fun stop()
}

/** [PositionPublisher] over [PositionForegroundService]'s two static entry points. */
internal class AndroidPositionPublisher(context: Context) : PositionPublisher {

    private val context = context.applicationContext

    override fun start(vehicleId: Ulid) {
        PositionForegroundService.start(context, vehicleId)
    }

    override fun stop() {
        PositionForegroundService.stop(context)
    }
}
