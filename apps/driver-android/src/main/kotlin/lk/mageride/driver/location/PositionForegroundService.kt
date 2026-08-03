package lk.mageride.driver.location

import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.location.Location
import android.os.Build
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.getSystemService
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import com.google.android.gms.location.LocationCallback
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationResult
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.Priority
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.driver.MainActivity
import lk.mageride.driver.R
import lk.mageride.driver.di.DriverDatabase
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.MqttSessionToken
import lk.mageride.shared.domain.auth.MqttSessionTokenManager
import lk.mageride.shared.mqtt.CommandDelivery
import lk.mageride.shared.mqtt.MqttCommand
import lk.mageride.shared.mqtt.MqttCommands
import lk.mageride.shared.mqtt.MqttConfig
import lk.mageride.shared.util.ReconnectBackoff
import org.koin.android.ext.android.inject
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * The Driver App's position publisher — D6' §3: *"Driver App client = HiveMQ (Android) … in a
 * native foreground service"*.
 *
 * **Why this cannot be anything smaller.** A publisher tied to an Activity stops the moment the
 * screen locks, and a driver's phone is in a mount with the screen off for most of a ride: the
 * ride's whole track would be lost while the app looked like it was working. A foreground service
 * with `FOREGROUND_SERVICE_TYPE_LOCATION` is the only construct Android offers that keeps
 * receiving fixes with the screen off and the app backgrounded.
 *
 * **Doze.** A foreground service is exempt from Doze's network and alarm restrictions, but Doze
 * still parks the CPU between maintenance windows, which would stretch a 4-second cadence into
 * minutes. The `PARTIAL_WAKE_LOCK` held for as long as the service runs is what keeps the cadence
 * the one D5' §5.2 asked for. It is expensive, and it is exactly as expensive as the requirement:
 * this runs only while the driver is deliberately online or on a trip, and [stop] releases it.
 *
 * **What it does not decide.** Every rule lives elsewhere and is called from here:
 * [PositionPipeline] for the cadence, the journal and the replay pacing; [MqttPositionTransport]
 * for the socket; `:shared`'s [MqttCommands] for the downlink. This class is the Android
 * lifecycle, the fix source, and the reconnect loop.
 */
// Fifteen functions, and the count is Android's rather than a design choice: four are Service
// lifecycle overrides, and the rest are the four concerns a foreground service cannot delegate —
// the socket, the fix subscription, the notification and the wake lock. Splitting them into
// collaborators would move the methods without removing one, and would put a seam through a
// lifecycle that has to be handled in one place.
@Suppress("TooManyFunctions")
@OptIn(ExperimentalTime::class)
internal class PositionForegroundService : LifecycleService() {

    private val mqttConfig: MqttConfig by inject()
    private val tokens: MqttSessionTokenManager by inject()
    private val databases: DriverDatabase by inject()

    private val transport by lazy { MqttPositionTransport(mqttConfig) }
    private val locations by lazy { LocationServices.getFusedLocationProviderClient(this) }

    private var pipeline: PositionPipeline? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var connectionJob: Job? = null
    private var drainJob: Job? = null
    private var vehicleId: Ulid? = null

    private val fixes = object : LocationCallback() {
        override fun onLocationResult(result: LocationResult) {
            val active = pipeline ?: return
            result.locations.forEach { active.onFix(now(), it.toFix()) }
            updateNotification(active.bufferedCount)
        }
    }

    // One return per way a start command can be answered — stop, no-vehicle, or run. Nesting them
    // would put the START_STICKY decision three levels in from the branch that earns it.
    @Suppress("ReturnCount")
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)

        when (intent?.action) {
            ACTION_STOP -> {
                stop()
                return START_NOT_STICKY
            }

            else -> {
                val vehicle = intent?.getStringExtra(EXTRA_VEHICLE_ID)
                if (vehicle == null) {
                    // Nothing to publish for. A service that started without a vehicle would hold
                    // a wake lock and a notification for a stream that cannot exist.
                    stop()
                    return START_NOT_STICKY
                }
                start(vehicle)
            }
        }

        // START_STICKY: if Android kills the process under memory pressure mid-ride, the service
        // is recreated. The intent is not redelivered, which is why the vehicle id is re-read from
        // the ride the app restores rather than assumed — see the null branch above.
        return START_STICKY
    }

    private fun start(vehicle: Ulid) {
        if (vehicleId == vehicle && pipeline != null) return
        vehicleId = vehicle

        // `startForeground` within five seconds of `startForegroundService`, before anything that
        // can block. Opening the database unwraps the SQLCipher key out of the Keystore, which is
        // fast but not instant, and an ANR here is a crash on the driver's first ride.
        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            notification(buffered = 0),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION
            } else {
                0
            },
        )

        acquireWakeLock()

        lifecycleScope.launch {
            // C018 deliberately does not bind an open database in the Koin graph — opening it is
            // suspending, and a `runBlocking` single would unwrap the key on whichever thread
            // first touched the graph. `DriverDatabase` is the app's deferred binding, so the
            // service opens it on its own scope and SCR-DA-034's inbox shares the handle (Δ C075)
            // rather than opening a second connection to the same encrypted file.
            val database = databases.get()
            pipeline = PositionPipeline(
                transport = transport,
                journal = GpsBufferJournal(database.gpsBuffer(vehicle)),
            )
            watchSessionToken()
            startDraining()
        }
    }

    /**
     * Reconnects whenever the MQTT session token changes.
     *
     * **EMQX validates the JWT at CONNECT only** (`MqttConfig`'s KDoc), so a rotation has no
     * effect on a live session — the socket has to be rebuilt. `MqttSessionTokenManager` renews at
     * `T−10 min` precisely so this reconnect happens with time to spare rather than at the moment
     * the credential dies.
     */
    private fun watchSessionToken() {
        connectionJob?.cancel()
        connectionJob = lifecycleScope.launch {
            tokens.token.collect { token ->
                if (token == null) {
                    transport.disconnect()
                    stopLocationUpdates()
                } else {
                    connectWithBackoff(token)
                }
            }
        }
    }

    /**
     * Connects, retrying on `ReconnectBackoff`'s schedule.
     *
     * The backoff is `:shared`'s and not a local loop because R-09 bounds how hard a city's worth
     * of handsets may retry after a broker restart — a fixed one-second retry from every driver in
     * Colombo is the reconnect storm itself.
     */
    private suspend fun connectWithBackoff(token: MqttSessionToken) {
        val backoff = ReconnectBackoff()
        while (lifecycleScope.isActive) {
            if (transport.connect(token)) {
                pipeline?.onReconnected(now())
                transport.subscribeCommands(::onCommand)
                startLocationUpdates()
                return
            }
            delay(backoff.next())
        }
    }

    /** A downlink command on `veh/{vehicleId}/cmd`. Only `setPosRate` changes anything here (R-07). */
    private fun onCommand(payload: ByteArray) {
        val delivery = MqttCommands.decode(payload, now())
        if (delivery !is CommandDelivery.Deliver) return
        val command = delivery.command
        if (command is MqttCommand.SetPosRate) {
            pipeline?.onCadenceHint(command, now())
            // The cadence changed, so the fix request has to be rebuilt — FusedLocation holds the
            // interval it was registered with, not the one we want next.
            startLocationUpdates()
        }
    }

    /**
     * (Re)registers for fixes at the cadence in force.
     *
     * `setMinUpdateIntervalMillis` at half the cadence lets a fix that arrives early still be
     * delivered — the pipeline discards it if the interval has not elapsed, and receiving one
     * early is cheaper than missing the first of a geofence burst by a whole tick.
     */
    @Suppress("MissingPermission") // SCR-DA-007 is the gate; the service is not started before it.
    private fun startLocationUpdates() {
        val interval = pipeline?.intervalAt(now()) ?: return
        val request = LocationRequest.Builder(Priority.PRIORITY_HIGH_ACCURACY, interval.inWholeMilliseconds)
            .setMinUpdateIntervalMillis(interval.inWholeMilliseconds / 2)
            // A first fix that waits for full accuracy can take tens of seconds under cover, and a
            // driver going online wants to appear on the map now. Accuracy arrives on the next tick.
            .setWaitForAccurateLocation(false)
            .build()

        runCatching {
            locations.removeLocationUpdates(fixes)
            locations.requestLocationUpdates(request, fixes, mainLooper)
        }
    }

    private fun stopLocationUpdates() {
        runCatching { locations.removeLocationUpdates(fixes) }
    }

    /**
     * Drains the offline backlog on a timer while the socket is up (R-17).
     *
     * A tick rather than a one-shot after reconnect, because the pacing rules hand the backlog out
     * in slices: the 2 s live-drain window, the 4:1 live preemption and the 20 msg/s ceiling all
     * mean a single pass would publish a handful and stop with hours of history still on disk.
     */
    private fun startDraining() {
        drainJob?.cancel()
        drainJob = lifecycleScope.launch {
            while (isActive) {
                delay(DRAIN_TICK)
                val active = pipeline ?: continue
                if (active.drainReplay(now()) > 0) updateNotification(active.bufferedCount)
            }
        }
    }

    private fun stop() {
        stopLocationUpdates()
        connectionJob?.cancel()
        drainJob?.cancel()
        transport.disconnect()
        pipeline = null
        vehicleId = null
        releaseWakeLock()
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        stop()
        super.onDestroy()
    }

    // ---- notification ------------------------------------------------------------------

    /**
     * The driver-visible proof that position is being shared.
     *
     * It says what is happening rather than that a service is running, and when the link is down
     * it says how many fixes are waiting — which is the difference between "this app is broken"
     * and "this app is holding my trip for me".
     */
    private fun notification(buffered: Long): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, PositionForegroundService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_menu_mylocation)
            .setContentTitle(getString(R.string.location_notification_title))
            .setContentText(
                if (buffered > 0) {
                    getString(R.string.location_notification_buffering, buffered.toInt())
                } else {
                    getString(R.string.location_notification_text)
                },
            )
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(open)
            .addAction(0, getString(R.string.location_notification_stop), stop)
            .build()
    }

    private fun updateNotification(buffered: Long) {
        val manager = getSystemService<android.app.NotificationManager>() ?: return
        manager.notify(NOTIFICATION_ID, notification(buffered))
    }

    // ---- wake lock ---------------------------------------------------------------------

    private fun acquireWakeLock() {
        if (wakeLock != null) return
        val power = getSystemService<PowerManager>() ?: return
        wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKE_LOCK_TAG).apply {
            setReferenceCounted(false)
            // No timeout: the lock's life is the service's, and the service's life is the
            // driver's shift. A timeout would silently drop the cadence mid-ride, which is the
            // exact failure the lock exists to prevent.
            acquire()
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
    }

    private fun now(): Timestamp = Clock.System.now()

    internal companion object {

        /** Created in `DriverApplication.onCreate` at `IMPORTANCE_LOW` — see that class. */
        const val CHANNEL_ID: String = "location_sharing"

        private const val NOTIFICATION_ID = 4201
        private const val WAKE_LOCK_TAG = "mageride:driver-position"
        private val DRAIN_TICK: Duration = 1.seconds

        private const val ACTION_STOP = "lk.mageride.driver.action.STOP_POSITION"
        private const val EXTRA_VEHICLE_ID = "vehicleId"

        /** Starts publishing for [vehicleId]. Idempotent — a second call for the same vehicle is a no-op. */
        fun start(context: Context, vehicleId: Ulid) {
            val intent = Intent(context, PositionForegroundService::class.java)
                .putExtra(EXTRA_VEHICLE_ID, vehicleId)
            context.startForegroundService(intent)
        }

        /** Stops publishing and announces `offline` on `veh/{vehicleId}/status`. */
        fun stop(context: Context) {
            val intent = Intent(context, PositionForegroundService::class.java).setAction(ACTION_STOP)
            context.startService(intent)
        }
    }
}

/** A platform fix as the pipeline's [Fix]. Android's sentinels become absent fields, not zeroes. */
@OptIn(ExperimentalTime::class)
private fun Location.toFix(): Fix = Fix(
    lat = latitude,
    lng = longitude,
    sampleTs = Timestamp.fromEpochMilliseconds(time),
    accuracyM = if (hasAccuracy()) accuracy.toDouble() else null,
    speedMps = if (hasSpeed()) speed.toDouble() else null,
    headingDeg = if (hasBearing()) bearing.toInt() else null,
)
