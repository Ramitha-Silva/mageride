package lk.mageride.passenger.shell

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
 * Feeds the offline banner (US-15.6, SCR-PA-032) and the live plane's reconnect loop, and nothing
 * else — the app never *decides* anything from it. A passenger in a tunnel keeps the last-known
 * markers on screen (US-15.2) and keeps whatever is queued queued; the banner is there so the
 * screen stops looking broken, which is the whole of that requirement (*"current screen preserved,
 * not full takeover"*).
 *
 * **`NET_CAPABILITY_VALIDATED`, not merely connected.** A captive-portal Wi-Fi at a café reports a
 * connected network that answers nothing, and that is exactly the case where a passenger watching
 * an empty map most needs to be told the app is not reaching the platform — SCR-PA-032's own
 * requirement is to *"distinguish 'no connectivity' from 'no vehicles'"*.
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

    /** A one-shot read, for a caller deciding whether it is worth dialling the hub at all. */
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
