package lk.mageride.driver.nav

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.AccountBalanceWallet
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Menu
import androidx.compose.ui.graphics.vector.ImageVector
import lk.mageride.driver.R

/**
 * The four bottom-navigation tabs, in the order `driver_android.html` prints them:
 * `[Home][Jobs][Wallet][≡]`.
 *
 * **AL-31: the dashboard has NO top-left hamburger.** [DriverTab.MenuTab] is the whole navigation
 * drawer's replacement — it is a peer of Home, not a corner affordance — and this list is the only
 * place the app defines a top-level destination. `BottomNavTest` asserts there are exactly four
 * and that Menu is one of them, so a later component cannot quietly reintroduce a drawer.
 */
internal enum class DriverTab(val route: DriverRoute, @param:StringRes val label: Int, val icon: ImageVector) {
    HomeTab(DriverRoute.Home, R.string.nav_home, Icons.Filled.Home),
    JobsTab(DriverRoute.Jobs, R.string.nav_jobs, Icons.AutoMirrored.Filled.List),
    WalletTab(DriverRoute.Wallet, R.string.nav_wallet, Icons.Filled.AccountBalanceWallet),

    /** AL-31's navigation entry point. */
    MenuTab(DriverRoute.Menu, R.string.nav_menu, Icons.Filled.Menu),
}
