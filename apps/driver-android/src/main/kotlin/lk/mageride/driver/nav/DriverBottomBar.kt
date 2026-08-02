package lk.mageride.driver.nav

import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource

/**
 * The M3 `NavigationBar` the four tabs render into.
 *
 * @param current The route path the NavHost is showing, so the right tab is selected. A route
 *   that is not a tab (an active ride, the capture camera) selects nothing, which is correct —
 *   those screens are pushed on top of a tab, not beside it.
 */
@Composable
internal fun DriverBottomBar(current: String?, onSelect: (DriverRoute) -> Unit) {
    NavigationBar(
        containerColor = MaterialTheme.colorScheme.surface,
        contentColor = MaterialTheme.colorScheme.onSurfaceVariant,
    ) {
        DriverTab.entries.forEach { tab ->
            val selected = current == tab.route.path
            NavigationBarItem(
                selected = selected,
                onClick = { if (!selected) onSelect(tab.route) },
                icon = { Icon(tab.icon, contentDescription = null) },
                label = { Text(stringResource(tab.label)) },
                colors = NavigationBarItemDefaults.colors(
                    selectedIconColor = MaterialTheme.colorScheme.onPrimaryContainer,
                    selectedTextColor = MaterialTheme.colorScheme.primary,
                    indicatorColor = MaterialTheme.colorScheme.primaryContainer,
                    unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant,
                    unselectedTextColor = MaterialTheme.colorScheme.onSurfaceVariant,
                ),
            )
        }
    }
}

/**
 * Whether [path] is one of the four tabs — i.e. whether the bottom bar belongs on that screen.
 *
 * Onboarding, the OTP screen and the document camera are full-bleed and must not show it; the
 * wireframes draw a `navbar` on the dashboard cluster and on nothing in cluster 1.
 */
internal fun isTabRoute(path: String?): Boolean = DriverTab.entries.any { it.route.path == path }
