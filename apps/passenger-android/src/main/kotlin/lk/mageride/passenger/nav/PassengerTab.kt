package lk.mageride.passenger.nav

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.automirrored.filled.ReceiptLong
import androidx.compose.material.icons.filled.Map
import androidx.compose.material.icons.filled.Menu
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.passenger.R

/**
 * The four bottom-navigation tabs, in the order `passenger_android.html` prints them:
 * `[🗺 Map][🧾 Trips][💬 Support][≡ Menu]`.
 *
 * **The passenger app HAS a hamburger, and this is not an AL-31 violation.** AL-31 is a rule about
 * the *driver* dashboard — "the dashboard has NO top-left hamburger; navigation is the bottom-nav
 * Menu tab" — because a driver taps with one thumb while a vehicle is moving. SCR-PA-033 is
 * explicit that the passenger's is *"a Material 3 modal navigation drawer opened from the ≡ menu /
 * 'Menu' tab"*, and the wireframe draws the `≡` in the live map's app bar on every cluster-2
 * screen. Two doors, one drawer.
 *
 * **[MenuTab] therefore carries no route.** It is the only tab that does not navigate: tapping it
 * opens the drawer over whatever is on screen, which is exactly what the wireframe draws — the map
 * is still underneath, the scrim is over it, and the Menu tab is the highlighted one. A route for
 * it would put a back-stack entry behind a sheet the passenger dismisses by tapping the scrim.
 * `NavigationShellTest` pins that null.
 */
internal enum class PassengerTab(val route: PassengerRoute?, @param:StringRes val label: Int, val icon: ImageVector) {
    MapTab(PassengerRoute.LiveMap, R.string.nav_map, Icons.Filled.Map),
    TripsTab(PassengerRoute.Trips, R.string.nav_trips, Icons.AutoMirrored.Filled.ReceiptLong),
    SupportTab(PassengerRoute.Support, R.string.nav_support, Icons.AutoMirrored.Filled.Chat),

    /** SCR-PA-033. Opens the drawer; never navigates. */
    MenuTab(null, R.string.nav_menu, Icons.Filled.Menu),
    ;

    companion object {
        /** The tabs that are destinations — everything except [MenuTab]. */
        val destinations: List<PassengerRoute> = entries.mapNotNull(PassengerTab::route)
    }
}

/**
 * Whether [path] is one of the three tab destinations — i.e. whether the bottom bar belongs on
 * that screen.
 *
 * Onboarding, the OTP screen, the booking flow and the full-screen takeovers (the call, the SOS,
 * the QR scanner) are full-bleed and must not show it; the wireframes draw a `navbar` on the
 * cluster-2 and cluster-4 home screens and on nothing else.
 */
internal fun isTabRoute(path: String?): Boolean = PassengerTab.destinations.any { it.path == path }
