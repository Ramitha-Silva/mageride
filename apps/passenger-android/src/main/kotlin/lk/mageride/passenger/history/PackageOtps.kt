package lk.mageride.passenger.history

/**
 * The two package OTPs, and the reason this class exists at all.
 *
 * **Neither OTP can be read back from the platform.**
 *
 * - The **pickup** OTP (P-07, US-20.4) comes back on `POST /v1/rides/request` and *"is shown once
 *   to the sender and never returned again; only its hash is stored"*. `RideDetail` has no field
 *   for it and `mobile_db_schema.md` §2.3's `rides` projection has no column.
 * - The **delivery** OTP (US-20.5) arrives only in the FCM `package_picked_up` payload — C076's
 *   `PushRouter` documents `data.deliveryOtp` — and there is no read for it either.
 *
 * SCR-PA-020 and SCR-PA-021 both draw one, and the Definition of Done asks that *"the OTPs shown
 * match the ones the driver must enter"*. So this holds what the app has actually been told, and
 * says nothing when it has not been told. **Process-lifetime only**: persisting a handover code
 * would need a schema change, and inventing one would be worse than admitting the gap — a screen
 * that showed a number the driver's app would reject is the one failure a handover cannot survive.
 *
 * Recorded in the C081 handoff as two contract gaps.
 */
internal class PackageOtps {

    private val pickup = mutableMapOf<String, String>()
    private val delivery = mutableMapOf<String, String>()

    /** C079's booking response, on its way past. The only time the sender's code exists. */
    fun rememberPickup(rideId: String, otp: String?) {
        otp?.takeIf { it.isNotBlank() }?.let { pickup[rideId] = it }
    }

    /** The `package_picked_up` push, on its way to SCR-PA-021. */
    fun rememberDelivery(rideId: String, otp: String?) {
        otp?.takeIf { it.isNotBlank() }?.let { delivery[rideId] = it }
    }

    /** The sender's code, or `null` — which is what a reinstalled app or a cold start has. */
    fun pickupFor(rideId: String): String? = pickup[rideId]

    /** The recipient's code, or `null`. */
    fun deliveryFor(rideId: String): String? = delivery[rideId]
}
