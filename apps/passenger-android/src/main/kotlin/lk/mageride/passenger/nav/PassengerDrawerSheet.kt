package lk.mageride.passenger.nav

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * The modal navigation drawer's content — SCR-PA-033.
 *
 * **The shell owns the container and the rows; C083 owns the identity block.** Navigation is the
 * shell's business in this module the way [PassengerRoute] and [PassengerTab] are, and a drawer
 * whose rows lived in a screen group would put the app's navigation in two files. What the shell
 * cannot supply is the header the wireframe draws — a name, a platform id and a phone number all
 * come from `GET /v1/users/me`, which is a screen's data layer — so [header] is a slot with a
 * brand-only default. C083 passes the real one and changes nothing else here.
 *
 * @param onOpen A row was tapped. The shell closes the drawer and navigates.
 * @param onLogOut The last row. See `PassengerShell` — it ends the session and lets the existing
 *   `RouteToLogin` subscriber clear the back stack, rather than navigating from here.
 * @param header The wireframe's `primaryContainer` identity block.
 */
@Composable
internal fun PassengerDrawerSheet(
    onOpen: (PassengerRoute) -> Unit,
    onLogOut: () -> Unit,
    modifier: Modifier = Modifier,
    header: @Composable ColumnScope.() -> Unit = { DefaultDrawerHeader() },
) {
    ModalDrawerSheet(
        modifier = modifier.width(ControlTokens.DrawerWidth),
        drawerContainerColor = MaterialTheme.colorScheme.background,
    ) {
        header()

        Column(
            modifier = Modifier.padding(
                horizontal = MageRideTheme.spacing.xs,
                vertical = MageRideTheme.spacing.xs,
            ),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            PassengerDrawerDestination.primary.forEach { DrawerRow(it, onOpen) }

            HorizontalDivider(
                modifier = Modifier.padding(
                    horizontal = MageRideTheme.spacing.xs,
                    vertical = MageRideTheme.spacing.xxs,
                ),
                color = MaterialTheme.colorScheme.outline,
            )

            PassengerDrawerDestination.secondary.forEach { DrawerRow(it, onOpen) }

            NavigationDrawerItem(
                selected = false,
                onClick = onLogOut,
                icon = {
                    Icon(
                        imageVector = Icons.AutoMirrored.Filled.Logout,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.RowIcon),
                    )
                },
                label = { Text(stringResource(R.string.drawer_log_out)) },
                // The wireframe prints this row in `error`, which is the whole visual difference
                // between it and the five above: it is the one row that does not open a screen.
                colors = NavigationDrawerItemDefaults.colors(
                    unselectedTextColor = MaterialTheme.colorScheme.error,
                    unselectedIconColor = MaterialTheme.colorScheme.error,
                ),
            )
        }
    }
}

@Composable
private fun DrawerRow(destination: PassengerDrawerDestination, onOpen: (PassengerRoute) -> Unit) {
    NavigationDrawerItem(
        selected = false,
        onClick = { onOpen(destination.route) },
        icon = {
            Icon(
                imageVector = destination.icon,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
            )
        },
        label = { Text(stringResource(destination.label)) },
        colors = NavigationDrawerItemDefaults.colors(
            unselectedTextColor = MaterialTheme.colorScheme.onSurface,
            unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant,
        ),
    )
}

/**
 * The header until C083 lands SCR-PA-033's identity block.
 *
 * Brand only, and deliberately no placeholder name: a greyed-out *"Your name"* in the shape of the
 * real thing is how a half-built screen ships looking finished. The `primaryContainer` block and
 * its avatar disc are the wireframe's, so the drawer has the right silhouette from the first
 * build.
 */
@Composable
private fun DefaultDrawerHeader() {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.primaryContainer)
            .padding(horizontal = MageRideTheme.spacing.md, vertical = MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Box(
            modifier = Modifier
                .size(ControlTokens.Avatar)
                .background(MaterialTheme.colorScheme.background, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.Person,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(ControlTokens.RowIcon),
            )
        }
        Text(
            text = stringResource(R.string.app_name),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onPrimaryContainer,
        )
    }
}
