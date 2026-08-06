package lk.mageride.passenger.location

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.os.Looper
import com.google.android.gms.location.LocationCallback
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationResult
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.Priority
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import lk.mageride.shared.data.models.GeoPoint

/**
 * Where the passenger is.
 *
 * **This app publishes nothing.** The driver's equivalent sits beside a foreground service that
 * owes the platform a cadence (D5' §5.2), holds a wake lock and runs through a whole shift; this
 * one is a plain foreground subscription with exactly three consumers, and all three are on
 * screen:
 *
 * 1. **The R-06 geocell anchor.** `PassengerLiveMap.onPosition` turns a fix into the nineteen
 *    res-7 cells the live map subscribes to, and into the 30 s hysteresis that decides when to
 *    move them.
 * 2. **MAP-02's accuracy circle** and §0.3's blue user dot.
 * 3. **A pickup default** — SCR-PA-009's and SCR-PA-013's "current location", and SCR-PA-011's
 *    live GPS pin.
 *
 * An interface with an Android implementation because on this build host `LocationServices` is a
 * stub whose every member throws, and because a view-model test needs to feed positions rather
 * than wait for a satellite.
 *
 * @property fixes Fixes while something is collecting, starting with the last known one. Cold:
 *   registering happens on the first collector and stops with the last, so a screen that is not
 *   visible does not hold a GPS subscription open.
 */
internal interface PassengerLocationSource {
    val fixes: Flow<PassengerFix>
}

/**
 * One fix.
 *
 * @property lat Degrees.
 * @property lng Degrees.
 * @property accuracyMetres Horizontal accuracy — MAP-02's circle radius. `0` when the platform
 *   did not report one, which draws no halo rather than a halo of unknown size.
 */
internal data class PassengerFix(val lat: Double, val lng: Double, val accuracyMetres: Double = 0.0)

/** The fix as a [GeoPoint] — what the geocell subscription and every booking body take. */
internal fun PassengerFix.asPoint(): GeoPoint = GeoPoint(lat = lat, lng = lng)

/** [PassengerLocationSource] over Google Play services' fused provider. */
internal class AndroidPassengerLocationSource(context: Context) : PassengerLocationSource {

    private val client = LocationServices.getFusedLocationProviderClient(context.applicationContext)

    // SCR-PA-005 is the gate and nothing on this path is reachable before it. A revoked permission
    // still throws, which is why both registrations are wrapped rather than assumed.
    @SuppressLint("MissingPermission")
    override val fixes: Flow<PassengerFix> = callbackFlow {
        val callback = object : LocationCallback() {
            override fun onLocationResult(result: LocationResult) {
                result.locations.forEach { trySend(it.asFix()) }
            }
        }

        // The last known fix first, so a map opened indoors joins the right nineteen cells
        // immediately instead of sitting over Colombo Fort until the first tick arrives.
        runCatching {
            client.lastLocation.addOnSuccessListener { last -> last?.let { trySend(it.asFix()) } }
        }

        val request = LocationRequest.Builder(Priority.PRIORITY_BALANCED_POWER_ACCURACY, INTERVAL_MS)
            .setMinUpdateIntervalMillis(INTERVAL_MS / 2)
            .setWaitForAccurateLocation(false)
            .build()

        runCatching { client.requestLocationUpdates(request, callback, Looper.getMainLooper()) }

        awaitClose { runCatching { client.removeLocationUpdates(callback) } }
    }

    private companion object {

        /**
         * Ten seconds, and `BALANCED_POWER_ACCURACY` rather than `HIGH_ACCURACY`.
         *
         * A passenger's position decides which **res-7 cell** they are in — a hexagon about 1.2 km
         * across — and moves the subscription at most once every thirty seconds anyway
         * (`GeoCells.BOUNDARY_HYSTERESIS`). Sampling GPS at the driver's four-second high-accuracy
         * cadence would spend a phone's battery to compute the same nineteen cells over and over.
         * The one screen that genuinely needs metre precision is SCR-PA-011's pickup pin, and it
         * asks for its own.
         */
        const val INTERVAL_MS = 10_000L
    }
}

/** A platform fix as a [PassengerFix]. Android's sentinel becomes a zero radius, not a fake one. */
private fun Location.asFix(): PassengerFix = PassengerFix(
    lat = latitude,
    lng = longitude,
    accuracyMetres = if (hasAccuracy()) accuracy.toDouble() else 0.0,
)
