package lk.mageride.driver.location

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
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.ExperimentalTime

/**
 * The handset's own GNSS, for a **screen**.
 *
 * **Not the publisher.** [PositionForegroundService] owns the fixes that reach the broker — its
 * cadence is D5' §5.2's, it holds a wake lock and it runs whether or not anything is on screen.
 * This is the other half: the dashboard needs the driver's own marker on the map (AL-31 — the home
 * map shows *only* their own vehicle, and `LiveMapScope.DriverHomeMap` joins no geocell group, so
 * the marker can come from nowhere else), `POST /v1/standby/online` needs a position in its body,
 * and SCR-DA-011 accumulates the journey's distance from it.
 *
 * A second `FusedLocationProviderClient` subscription is not a second GPS: the platform multiplexes
 * requests and hands the same fixes to both. What it is, is a subscription that lives and dies with
 * a composition rather than with a shift — which is exactly why the service cannot supply it.
 *
 * An interface with an Android implementation for the same reason as `ActiveVehicleStore`: on this
 * host `LocationServices` is a stub whose every member throws.
 */
internal interface DriverLocationSource {

    /**
     * Fixes while something is collecting, starting with the last known one.
     *
     * Cold: registering happens on the first collector and stops with the last. A screen that is
     * not visible must not hold a GPS subscription open.
     */
    val fixes: Flow<Fix>
}

/** The driver's position as a [GeoPoint] — what `GoOnlineRequest` and the Directional card take. */
internal fun Fix.asPoint(): GeoPoint = GeoPoint(lat = lat, lng = lng)

/** [DriverLocationSource] over Google Play services' fused provider. */
internal class AndroidDriverLocationSource(context: Context) : DriverLocationSource {

    private val client = LocationServices.getFusedLocationProviderClient(context.applicationContext)

    // SCR-DA-007 is the gate and nothing on this path is reachable before it; the lint suppression
    // is the same one `PositionForegroundService` carries, for the same reason. A revoked
    // permission still throws, which is why the registration is wrapped.
    @SuppressLint("MissingPermission")
    override val fixes: Flow<Fix> = callbackFlow {
        val callback = object : LocationCallback() {
            override fun onLocationResult(result: LocationResult) {
                result.locations.forEach { trySend(it.asFix()) }
            }
        }

        // The last known fix first, so a dashboard opened in a car park draws the driver where they
        // are instead of over Colombo Fort while the first tick is waited for.
        runCatching {
            client.lastLocation.addOnSuccessListener { last -> last?.let { trySend(it.asFix()) } }
        }

        val request = LocationRequest.Builder(Priority.PRIORITY_HIGH_ACCURACY, SCREEN_INTERVAL_MS)
            .setMinUpdateIntervalMillis(SCREEN_INTERVAL_MS / 2)
            // Same argument the service makes: a driver going online wants to appear on the map
            // now, and accuracy arrives on the next tick.
            .setWaitForAccurateLocation(false)
            .build()

        runCatching { client.requestLocationUpdates(request, callback, Looper.getMainLooper()) }

        awaitClose { runCatching { client.removeLocationUpdates(callback) } }
    }

    private companion object {

        /**
         * Four seconds — D5' §5.1's base cadence.
         *
         * Deliberately the *slow* end: this subscription draws a marker and accumulates a
         * kilometre count, and the publisher beside it is the one that owes the platform a rate.
         */
        const val SCREEN_INTERVAL_MS = 4_000L
    }
}

/** A platform fix as a [Fix]. Android's sentinels become absent fields, not zeroes. */
@OptIn(ExperimentalTime::class)
private fun Location.asFix(): Fix = Fix(
    lat = latitude,
    lng = longitude,
    sampleTs = Timestamp.fromEpochMilliseconds(time),
    accuracyM = if (hasAccuracy()) accuracy.toDouble() else null,
    speedMps = if (hasSpeed()) speed.toDouble() else null,
    headingDeg = if (hasBearing()) bearing.toInt() else null,
)
