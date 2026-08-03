package lk.mageride.driver.menu

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ReceiptLong
import androidx.compose.material.icons.outlined.AddCircleOutline
import androidx.compose.material.icons.outlined.DirectionsBus
import androidx.compose.material.icons.outlined.DirectionsCar
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.SupportAgent
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.driver.R
import lk.mageride.driver.nav.DriverRoute

/**
 * The eight destinations SCR-DA-036 lists, in the order the wireframe prints them.
 *
 * **This is AL-31's whole replacement for the hamburger.** The list lives here rather than inside
 * the composable so a later group adding a destination adds a row to a table, and so the coverage
 * test can walk it. Every entry resolves to a real [DriverRoute]; there are no dead rows.
 *
 * @property route Where the row goes.
 * @property label Trilingual copy.
 * @property icon The wireframe's leading glyph.
 */
internal enum class MenuDestination(val route: DriverRoute, @param:StringRes val label: Int, val icon: ImageVector) {
    /** SCR-DA-026 (C069). */
    MyVehicles(DriverRoute.Vehicles, R.string.menu_my_vehicles, Icons.Outlined.DirectionsCar),

    /** SCR-DA-004 — the Mode-C wizard, which AL-30 resumes or restarts (C069). */
    VehicleOnboarding(DriverRoute.VehicleOnboarding, R.string.menu_vehicle_onboarding, Icons.Outlined.AddCircleOutline),

    /** SCR-DA-027 (C074). */
    TrackerPairing(DriverRoute.TrackerPairing, R.string.menu_tracker_pairing, Icons.Outlined.Wifi),

    /** SCR-DA-028 — Mode B sharing (C074). */
    Sharing(DriverRoute.Sharing, R.string.menu_sharing, Icons.Outlined.DirectionsBus),

    /** SCR-DA-029 (C074). */
    Profile(DriverRoute.Profile, R.string.menu_profile, Icons.Outlined.Person),

    /** SCR-DA-030 — ride history and the rate-passenger sheet (C074). */
    RideHistory(DriverRoute.RideHistory, R.string.menu_ride_history, Icons.AutoMirrored.Outlined.ReceiptLong),

    /** SCR-DA-033 — support and the daily-fee refund request (C075). */
    Support(DriverRoute.Support, R.string.menu_support, Icons.Outlined.SupportAgent),

    /** SCR-DA-034 (C075). */
    Notifications(DriverRoute.Notifications, R.string.menu_notifications, Icons.Outlined.Notifications),
}
