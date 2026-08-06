package lk.mageride.passenger.nav

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.filled.AirportShuttle
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.VpnKey
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.passenger.R

/**
 * SCR-PA-033's rows, as a table.
 *
 * The wireframe's `States:` line is the source and it is exhaustive: *"Items route to: **Private
 * transport → SCR-PA-024**, **My subscriptions → SCR-PA-025**, **Saved addresses → SCR-PA-026**,
 * **Profile & settings → SCR-PA-027** (plus Help & support, Log out)"*. Five destinations and one
 * action; `PassengerDrawerTest` pins the count and the order so a later screen group cannot
 * quietly add a seventh row or point one at the nearest existing screen.
 *
 * *Help & support* is the same destination as bottom-nav tab 3, and that is the wireframe's own
 * choice rather than an oversight — a passenger looking for help finds it in both places.
 *
 * [ModeBRequest][PassengerRoute.ModeBRequest] is entered here with **no vehicle id**: from the
 * drawer there is no marker to read one from and the passenger types it (AL-23).
 */
internal enum class PassengerDrawerDestination(
    val route: PassengerRoute,
    @param:StringRes val label: Int,
    val icon: ImageVector,
) {
    /** SCR-PA-024 — 🚐 Private transport. */
    PrivateTransport(PassengerRoute.ModeBRequest(), R.string.drawer_private_transport, Icons.Filled.AirportShuttle),

    /** SCR-PA-025 — 🔑 My subscriptions. */
    Subscriptions(PassengerRoute.Subscriptions, R.string.drawer_subscriptions, Icons.Filled.VpnKey),

    /** SCR-PA-026 — ★ Saved addresses. */
    SavedAddresses(PassengerRoute.SavedAddresses, R.string.drawer_saved_addresses, Icons.Filled.Star),

    /** SCR-PA-027 — ⚙ Profile & settings. */
    Settings(PassengerRoute.Settings, R.string.drawer_settings, Icons.Filled.Settings),

    /** SCR-PA-030 — 💬 Help & support, below the divider. */
    Support(PassengerRoute.Support, R.string.drawer_support, Icons.AutoMirrored.Filled.Chat),
    ;

    companion object {
        /** The rows above the divider. */
        val primary: List<PassengerDrawerDestination> =
            listOf(PrivateTransport, Subscriptions, SavedAddresses, Settings)

        /** The rows below it. */
        val secondary: List<PassengerDrawerDestination> = listOf(Support)
    }
}
