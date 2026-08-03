package lk.mageride.driver.shell

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import lk.mageride.driver.R
import lk.mageride.driver.di.DriverDatabase
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.vehicle.ActiveVehicleStore
import org.koin.compose.koinInject
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * How many GPS samples this handset is holding for the live vehicle (R-17, US-15.1).
 *
 * `gps_buffer` is written by `PositionForegroundService` through `GpsBufferJournal` whether or not
 * anything is on screen, and this is the read side: **the last-known state SCR-DA-035 shows instead
 * of an error**. Per vehicle, because the table is — `seq` is scoped to `(vehicle_id, seq)` and a
 * global count would mix a backlog left behind under a vehicle the driver changed away from.
 *
 * Zero is the answer when there is no live vehicle: a driver who has not chosen one is not
 * publishing, so there is nothing buffered under any id this screen could name.
 */
internal class BufferedSampleCounter(private val databases: DriverDatabase, private val vehicles: ActiveVehicleStore) {

    /** The backlog for the live vehicle. Blocking underneath, so it hops to [Dispatchers.IO]. */
    suspend fun buffered(): Long = withContext(Dispatchers.IO) {
        val vehicleId = vehicles.activeVehicleId ?: return@withContext 0L
        runCatching { databases.get().gpsBuffer(vehicleId).size() }.getOrDefault(0L)
    }
}

/**
 * **SCR-DA-035's buffered-samples card** — *"128 queued · will replay on reconnect"*.
 *
 * The wireframe draws it inside the home sheet under the offline banner: the map greys out, the
 * banner says the connection is gone and this says what the app is doing about it. That is the
 * whole of *"offline mode shows cached state rather than errors"* — the samples are on disk, the
 * `seq` counter has not rewound, and `veh/{vehicleId}/pos/replay` drains them the moment the socket
 * is back (R-17, QoS 1, monotonic `seq`).
 *
 * **Self-contained on purpose.** It reads connectivity and the backlog itself rather than taking
 * them as parameters, so the two home sheets — SCR-DA-010's and SCR-DA-011's — each add one line
 * instead of threading two new fields through C070's state. It draws nothing at all while the
 * handset is online or the backlog is empty, which is every ordinary moment.
 */
@Composable
internal fun BufferedSamplesCard(modifier: Modifier = Modifier) {
    val connectivity = koinInject<ConnectivityMonitor>()
    val counter = koinInject<BufferedSampleCounter>()

    val online by connectivity.isOnline.collectAsStateWithLifecycle(initialValue = true)
    var queued by remember { mutableLongStateOf(0L) }

    // Polled rather than observed: `gps_buffer` is written from a foreground service in the same
    // process and SQLDelight's Query.Listener would fire on every fix — four times a minute of
    // recomposition for a number that only has to be roughly right.
    LaunchedEffect(online) {
        while (!online) {
            queued = counter.buffered()
            delay(POLL)
        }
        queued = 0
    }

    if (online || queued <= 0) return

    NoticeCard(accent = MageRideTheme.status.warning, modifier = modifier) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = stringResource(R.string.offline_buffered_title),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = stringResource(R.string.offline_buffered_replay),
                    modifier = Modifier.padding(top = MageRideTheme.spacing.xxs),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            StatusPill(
                label = stringResource(R.string.offline_buffered_count, queued),
                tone = StatusTone.PENDING,
            )
        }
    }
}

/**
 * Five seconds.
 *
 * The backlog grows at the D5' §5.2 cadence — one row every one to eight seconds — so a faster
 * poll would redraw the same number, and a slower one would make a driver watching the count wonder
 * whether anything was being kept at all.
 */
private val POLL: Duration = 5.seconds
