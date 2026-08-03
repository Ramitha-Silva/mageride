package lk.mageride.driver.menu

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * **SCR-DA-036 · the menu drawer.**
 *
 * The wireframe draws a Material 3 **modal navigation drawer** with a `primaryContainer` header —
 * avatar, name, level badge, driver id and rating — over the dashboard, and eight rows under it.
 *
 * **AL-31 is why it is a tab rather than a corner affordance.** *"The dashboard has NO top-left
 * hamburger; navigation is the bottom-nav Menu tab"*, and `DriverTab.MenuTab` is a peer of Home. So
 * this destination **is** the drawer: the sheet is drawn at its documented width against a scrim,
 * and the scrim takes the driver back where they came from — which is exactly what "scrim tap /
 * swipe-left closes" means once the drawer is a destination rather than an overlay.
 *
 * @param onClose The scrim tap. Goes back rather than to Home: a driver who opened Menu from the
 *   Wallet tab expects to return to the Wallet tab.
 */
@Composable
internal fun MenuScreen(onOpen: (DriverRoute) -> Unit, onClose: () -> Unit, modifier: Modifier = Modifier) {
    Row(modifier = modifier.fillMaxSize()) {
        ModalDrawerSheet(modifier = Modifier.width(ControlTokens.DrawerWidth)) {
            MenuHeader()

            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
            ) {
                MenuDestination.entries.forEach { destination ->
                    NavigationDrawerItem(
                        label = { Text(text = stringResource(destination.label)) },
                        icon = { Icon(imageVector = destination.icon, contentDescription = null) },
                        selected = false,
                        onClick = { onOpen(destination.route) },
                        modifier = Modifier.padding(NavigationDrawerItemDefaults.ItemPadding),
                    )
                }
            }
        }

        // The wireframe's scrim over the dashboard. Tapping it is the documented way out.
        Surface(
            modifier = Modifier
                .weight(1f)
                .fillMaxSize(),
            color = MaterialTheme.colorScheme.scrim.copy(alpha = SCRIM_ALPHA),
            onClick = onClose,
            content = {},
        )
    }
}

/**
 * The drawer's `primaryContainer` header.
 *
 * The wireframe prints a name, an `L3` badge, a `DRV-22011` id and `★4.8`. **The rating has no
 * app-facing read** (see `HomeScreen`'s KDoc) and the driver id and display name come from the
 * profile group's own read (C073, SCR-DA-029), which this component does not own. The header is
 * therefore the avatar and the label the shell already has — a wrong name is worse than none, and
 * inventing a rating would be worse still. Recorded in the C070 handoff.
 */
@Composable
private fun MenuHeader(modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.md),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Surface(
            modifier = Modifier.size(ControlTokens.AvatarSmall),
            shape = CircleShape,
            color = MaterialTheme.colorScheme.primaryContainer,
        ) {
            Box(contentAlignment = Alignment.Center) {
                Icon(
                    imageVector = Icons.Outlined.Person,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
        }
        Text(
            text = stringResource(R.string.app_name),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** D2' §0.2's scrim opacity — the same figure the offline map overlay uses. */
private const val SCRIM_ALPHA = 0.45f
