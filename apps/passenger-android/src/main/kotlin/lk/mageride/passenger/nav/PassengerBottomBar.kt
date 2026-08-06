package lk.mageride.passenger.nav

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
 *   that is not a tab (an active ride, the booking form) selects nothing, which is correct —
 *   those screens are pushed on top of a tab, not beside it.
 * @param drawerOpen Whether the drawer is showing. It is what selects the Menu tab, because the
 *   Menu tab is not a destination and the NavHost's route can never say so — see [PassengerTab].
 * @param onSelect A tab that names a destination was tapped.
 * @param onOpenMenu The Menu tab was tapped.
 */
@Composable
internal fun PassengerBottomBar(
    current: String?,
    drawerOpen: Boolean,
    onSelect: (PassengerRoute) -> Unit,
    onOpenMenu: () -> Unit,
) {
    NavigationBar(
        containerColor = MaterialTheme.colorScheme.surface,
        contentColor = MaterialTheme.colorScheme.onSurfaceVariant,
    ) {
        PassengerTab.entries.forEach { tab ->
            val route = tab.route
            val selected = if (route == null) drawerOpen else !drawerOpen && current == route.path
            NavigationBarItem(
                selected = selected,
                onClick = {
                    when {
                        route == null -> onOpenMenu()
                        !selected -> onSelect(route)
                    }
                },
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
