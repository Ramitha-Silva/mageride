package lk.mageride.driver.menu

import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.ui.component.DriverHeader
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-036 · the menu drawer.**
 *
 * The wireframe draws a Material 3 **modal navigation drawer** with a `primaryContainer` header —
 * avatar, name, level badge, driver id and rating — over the dashboard, and eight rows under it.
 *
 * **AL-31 is why it is a tab rather than a corner affordance.** *"The dashboard has NO top-left
 * hamburger; navigation is the bottom-nav Menu tab"*, and `DriverTab.MenuTab` is a peer of Home. So
 * this destination **is** the drawer: the sheet is drawn at its documented width against a scrim,
 * and the scrim takes the driver back where they came from.
 *
 * **Δ MCS-24 — both halves of "scrim tap / swipe-left closes" are wired now.** Only the tap was. On
 * a sheet pinned to the left edge, dragging it leftward is the gesture a driver reaches for first,
 * and it did nothing at all — no movement, no rubber-band, no hint that the tap was the way out.
 *
 * **Δ MCS-24 — the header is the DRIVER, not the app.** It printed `app_name`, and the reason was
 * recorded and was a good one: this component did not own the profile read, so *"a wrong name is
 * worse than none, and inventing a rating would be worse still"*. That reasoning survives — the
 * rating is still drawn only if one exists, and none does — but the premise does not.
 * [MenuViewModel] owns the read now, so the name, the level and the live vehicle's plate are this
 * screen's to show. What the header must never do is guess, and it does not.
 *
 * @param onClose The scrim tap, and now the swipe. Goes back rather than to Home: a driver who
 *   opened Menu from the Wallet tab expects to return to the Wallet tab.
 */
@Composable
internal fun MenuScreen(onOpen: (DriverRoute) -> Unit, onClose: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: MenuViewModel = koinViewModel()
    val header by viewModel.header.collectAsStateWithLifecycle()

    Row(modifier = modifier.fillMaxSize()) {
        ModalDrawerSheet(
            modifier = Modifier
                .width(ControlTokens.DrawerWidth)
                // The whole gesture is accumulated and judged on RELEASE rather than acted on per
                // event, so a drag that wanders left and comes back does not close: a driver who
                // starts a swipe and changes their mind keeps their menu.
                .pointerInput(Unit) {
                    var travelled = 0f

                    detectHorizontalDragGestures(
                        onDragStart = { travelled = 0f },
                        onDragCancel = { travelled = 0f },
                        onDragEnd = {
                            if (travelled <= -CLOSE_SWIPE_FRACTION * size.width) onClose()
                            travelled = 0f
                        },
                        onHorizontalDrag = { change, dragAmount ->
                            change.consume()
                            travelled += dragAmount
                        },
                    )
                },
        ) {
            DriverHeader(
                state = header,
                modifier = Modifier.padding(
                    horizontal = MageRideTheme.spacing.md,
                    vertical = MageRideTheme.spacing.sm,
                ),
            )

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
 * How far left the sheet must be dragged before releasing closes it (Δ MCS-24).
 *
 * A fraction of the sheet rather than a fixed distance: the drawer is [ControlTokens.DrawerWidth]
 * wide on every handset, so a third of it is a third of the same control everywhere. Low enough to
 * be a flick, high enough that a horizontal wobble during a vertical scroll of the rows below does
 * not dismiss the screen.
 */
private const val CLOSE_SWIPE_FRACTION = 0.33f

/** D2' §0.2's scrim opacity — the same figure the offline map overlay uses. */
private const val SCRIM_ALPHA = 0.45f
