package lk.mageride.driver.tracker

import lk.mageride.driver.location.PositionPublisher
import lk.mageride.shared.data.models.Ulid

/**
 * **The C074 fence: once a tracker is paired, the phone stops ingesting GPS for that vehicle.**
 *
 * D2' §SCR-DA-027's states put it plainly — *"after pairing/assignment the phone stops publishing
 * GPS for this vehicle — the device becomes the single active publisher"* (US-3.6) — and
 * provisioning-svc says the same thing from the server side: `POST /v1/trackers/{imei}/switch-source`
 * is *"exactly one publisher at a time … switching to `hardware` stops the driver app's MQTT
 * publish and vice versa, so the position stream never interleaves two clocks"*.
 *
 * **It is a decorator on the publisher seam rather than a check in a screen**, because there are
 * three doors onto `PositionForegroundService` and a rule written at one of them would be missing
 * from the other two: SCR-DA-010's go-online toggle, SCR-DA-011's Start Journey and US-5.10's
 * Restart all call [PositionPublisher.start]. Binding this over `AndroidPositionPublisher` in the
 * Koin graph closes all three at once, and `HomeViewModel` needs no knowledge of trackers.
 *
 * **The vehicle id is the whole test**, and it is the right one: the MQTT username *is* the vehicle
 * id (D6' §3), so "this handset must not publish for that vehicle" is exactly what the broker would
 * otherwise see two sessions of. A driver with a tracked bus and an untracked tuk goes online on
 * the tuk normally.
 *
 * [stop] is **never** gated. Stopping a publisher that is not running is a no-op, and a gate that
 * could swallow a stop would leave a handset publishing for a vehicle it had just been paired away
 * from — the exact state this class exists to prevent.
 */
internal class TrackerPositionPublisher(
    private val delegate: PositionPublisher,
    private val bindings: TrackerBindingStore,
) : PositionPublisher {

    /**
     * Starts publishing for [vehicleId], **unless a tracker is bound to it**.
     *
     * The refusal is silent by design: this is not an error the driver can act on, it is the
     * hardware doing its job. What says so is SCR-DA-027's own paired card and SCR-DA-011's
     * *"Journey started by the device"* banner — the dashboard reports the device's doing (AL-32).
     */
    override fun start(vehicleId: Ulid) {
        if (isTracked(vehicleId)) return
        delegate.start(vehicleId)
    }

    override fun stop() {
        delegate.stop()
    }

    /** Whether the hardware device is the active publisher for [vehicleId]. */
    fun isTracked(vehicleId: Ulid): Boolean = bindings.bindingFor(vehicleId) != null
}
