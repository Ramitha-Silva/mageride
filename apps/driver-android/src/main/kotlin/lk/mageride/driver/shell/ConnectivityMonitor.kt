package lk.mageride.driver.shell

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import androidx.core.content.getSystemService
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged

/**
 * Whether the handset has a validated internet path.
 *
 * Feeds the offline banner (US-15.6, SCR-DA-032) and nothing else — the app never *decides*
 * anything from it. A driver in a tunnel keeps sampling GPS, keeps buffering to `gps_buffer` and
 * keeps queuing outbox commands regardless of what this says; the banner is there so the screen
 * stops looking broken, which is the whole of that requirement ("current screen preserved, not
 * full takeover").
 *
 * **`NET_CAPABILITY_VALIDATED`, not merely connected.** A captive-portal Wi-Fi at a filling
 * station reports a connected network that answers nothing, and that is exactly the case where a
 * driver most needs to be told the app is not reaching the platform.
 */
internal class ConnectivityMonitor(context: Context) {

    private val manager = context.applicationContext.getSystemService<ConnectivityManager>()

    /**
     * Emits `true` while a validated network is up.
     *
     * Starts from [isOnlineNow] rather than waiting for the first callback: `registerNetworkCallback`
     * does replay `onAvailable` for existing networks, but not before the first frame is drawn, and
     * a banner that flashes "offline" on every cold start is worse than no banner.
     */
    val isOnline: Flow<Boolean> = callbackFlow {
        val connectivity = manager
        if (connectivity == null) {
            // No ConnectivityManager means a stripped or emulated system image. Claiming offline
            // would put a permanent banner over an app that works.
            trySend(true)
            awaitClose { }
            return@callbackFlow
        }

        trySend(isOnlineNow())

        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                trySend(true)
            }

            override fun onLost(network: Network) {
                trySend(isOnlineNow())
            }

            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
                trySend(capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED))
            }
        }

        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            .build()

        connectivity.registerNetworkCallback(request, callback)
        awaitClose { connectivity.unregisterNetworkCallback(callback) }
    }.distinctUntilChanged()

    /** A one-shot read, for the foreground service deciding whether to publish or buffer. */
    // One guard per way the answer can be known early: no manager, no active network, no
    // capabilities. Each is a different fact about the handset, and chaining them into one
    // expression would report all three as the same thing.
    @Suppress("ReturnCount")
    fun isOnlineNow(): Boolean {
        val connectivity = manager ?: return true
        val active = connectivity.activeNetwork ?: return false
        val capabilities = connectivity.getNetworkCapabilities(active) ?: return false
        return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
            capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
    }
}
