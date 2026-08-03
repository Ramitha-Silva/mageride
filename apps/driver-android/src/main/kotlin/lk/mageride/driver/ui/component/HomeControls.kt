package lk.mageride.driver.ui.component

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ProgressIndicatorDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.CtaTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * The wireframe's `.sheet` — a rounded, elevated panel pinned to the bottom of a map screen.
 *
 * Four of C070's screens draw one (SCR-DA-010, 011, 015 and 035's buffered-samples card), always
 * with the same `rCard 24` top corners and the same grab handle, so it is one composable rather
 * than four `Surface`s that drift apart. It is deliberately **not** an M3 `BottomSheetScaffold`:
 * the wireframe's sheet is always visible and never dismissible — the map behind it is context, not
 * a screen the driver can get back to by swiping this away.
 */
@Composable
internal fun DashboardSheet(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(
            topStart = MageRideTheme.radius.card,
            topEnd = MageRideTheme.radius.card,
        ),
        color = MaterialTheme.colorScheme.background,
        tonalElevation = MageRideTheme.elevation.level2,
        shadowElevation = MageRideTheme.elevation.level3,
    ) {
        Column(
            modifier = Modifier.padding(
                start = MageRideTheme.spacing.sm,
                end = MageRideTheme.spacing.sm,
                top = MageRideTheme.spacing.xs,
                bottom = MageRideTheme.spacing.sm,
            ),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Box(
                modifier = Modifier
                    .align(Alignment.CenterHorizontally)
                    .size(width = ControlTokens.SheetHandleWidth, height = ControlTokens.SheetHandleHeight)
                    .background(MaterialTheme.colorScheme.outline, CircleShape),
            )
            content()
        }
    }
}

/**
 * SCR-DA-010's **◉ ONLINE — Mode C** bar (US-6A.1).
 *
 * A 64 dp bar rather than a `Switch` in a row: it is the single most-tapped control in the app and
 * the wireframe gives it the whole width. The colour transition is D2' §SCR-DA-010's *"toggle color
 * transition"* animation, and it runs between `success` and the neutral surface so the state is
 * legible without reading the label.
 *
 * **[enabled] is US-9.6's gate**, not a styling choice: with no eligible vehicle the bar is inert
 * and the caller shows the *"Add or get assigned a vehicle"* prompt underneath it.
 */
@Composable
internal fun OnlineToggle(
    label: String,
    online: Boolean,
    enabled: Boolean,
    busy: Boolean,
    onToggle: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    val container by animateColorAsState(
        targetValue = when {
            !enabled -> MaterialTheme.colorScheme.surfaceVariant
            online -> MageRideTheme.status.success
            else -> MaterialTheme.colorScheme.surfaceVariant
        },
        label = "online-toggle-container",
    )
    val content = if (online && enabled) MageRideTheme.status.onStatus else MaterialTheme.colorScheme.onSurfaceVariant

    Surface(
        modifier = modifier
            .fillMaxWidth()
            .height(ControlTokens.BigToggle),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        color = container,
        contentColor = content,
        enabled = enabled && !busy,
        onClick = { onToggle(!online) },
    ) {
        Row(
            modifier = Modifier.padding(horizontal = MageRideTheme.spacing.md),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(text = label, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            if (busy) {
                CircularProgressIndicator(
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    color = content,
                    strokeWidth = ProgressIndicatorDefaults.CircularStrokeWidth,
                )
            } else {
                Switch(
                    checked = online,
                    onCheckedChange = onToggle,
                    enabled = enabled,
                    colors = SwitchDefaults.colors(
                        checkedThumbColor = MageRideTheme.status.onStatus,
                        checkedTrackColor = MageRideTheme.status.success,
                    ),
                )
            }
        }
    }
}

/**
 * The wireframe's `.banner` — a full-bleed strip under the app bar in one of four tones.
 *
 * The daily-fee chip (`succ`), the GPS-ignition notice (`info`), the low-balance nudge (`warn`) and
 * the offline strip (`err`) are all this control, which is what keeps them the same height and the
 * same padding on three different screens.
 *
 * @param accent The role colour. `MageRideTheme.status.success` / `.warning`,
 *   `MaterialTheme.colorScheme.secondary` or `.error`.
 */
@Composable
internal fun DashboardBanner(text: String, accent: Color, modifier: Modifier = Modifier, icon: ImageVector? = null) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(accent.copy(alpha = BANNER_TINT))
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        if (icon != null) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.ChipIcon),
                tint = accent,
            )
        }
        Text(text = text, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurface)
    }
}

/**
 * SCR-DA-014's fifteen-second ring.
 *
 * @param progress `1.0` at the moment of the offer down to `0.0` at the deadline — `RideOffer`'s
 *   own `progress(now)`, which is derived from the **deadline** rather than from when this device
 *   saw the push. A push that took two seconds to arrive shows thirteen seconds of ring.
 * @param urgent The last five seconds, which D2' colours red.
 */
@Composable
internal fun CountdownRing(progress: Float, seconds: Int, urgent: Boolean, modifier: Modifier = Modifier) {
    val colour by animateColorAsState(
        targetValue = if (urgent) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
        label = "countdown-ring",
    )
    Box(modifier = modifier.size(ControlTokens.CountdownRing), contentAlignment = Alignment.Center) {
        CircularProgressIndicator(
            progress = { progress },
            modifier = Modifier.size(ControlTokens.CountdownRing),
            color = colour,
            strokeWidth = ControlTokens.CountdownStroke,
            trackColor = MaterialTheme.colorScheme.surfaceVariant,
            strokeCap = StrokeCap.Round,
        )
        Text(
            text = seconds.toString(),
            style = MaterialTheme.typography.headlineMedium,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * The full-width action D2' colours by consequence rather than by brand.
 *
 * §SCR-DA-011 gives Start Journey `success` and End Journey `error`; §SCR-DA-015's table gives
 * *"Start ride"* `success` and *"End ride"* `error`. `MageRideCta` is §0.2's **primary** CTA and is
 * deliberately one colour, so a green or red bar is this — same height, same radius, same
 * typography, one token away from the orange one.
 */
@Composable
internal fun StatusCta(
    label: String,
    accent: Color,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Button(
        onClick = onClick,
        modifier = modifier
            .fillMaxWidth()
            .height(CtaTokens.Height),
        enabled = enabled,
        shape = RoundedCornerShape(CtaTokens.Radius),
        colors = ButtonDefaults.buttonColors(
            containerColor = accent,
            contentColor = MageRideTheme.status.onStatus,
            disabledContainerColor = MaterialTheme.colorScheme.surfaceVariant,
            disabledContentColor = MaterialTheme.colorScheme.outlineVariant,
        ),
    ) {
        Text(text = label, style = MaterialTheme.typography.titleMedium)
    }
}

/**
 * The solid badges SCR-DA-014 stacks above the fare — *"Third-party booking"* (P-05),
 * *"Package · M"* (US-20.3) and *"Directional"* (DT-08) — and SCR-DA-010's `L3` level badge.
 *
 * Solid rather than tinted, like `ModeCBadge`: each is an identity the driver has to read in a
 * glance at a phone in a windscreen mount, not a status they can go and look up.
 */
@Composable
internal fun SolidBadge(label: String, accent: Color, modifier: Modifier = Modifier) {
    Text(
        text = label,
        modifier = modifier
            .background(accent, RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = MageRideTheme.status.onStatus,
    )
}

/**
 * The wireframe's `chip sm` with a coloured `cdot` — *"Three-wheeler · ABC-1234"* (US-9.6).
 *
 * The dot is MAP-03's per-type colour, so the chip on the dashboard is the colour that vehicle is
 * on the map. Same table as My Vehicles' row (`VehicleColors.forType`), on purpose.
 *
 * **Δ C074 — the same chip is also a control.** SCR-DA-028's full-device-width vehicle selector is
 * the wireframe's `chip on sm`: the same shape, with a `primaryContainer` fill and a primary border
 * when it is the selected one. Passing [onClick] is what makes it selectable; a chip that is only
 * reporting state (SCR-DA-010's) leaves it null and stays unfocusable.
 *
 * @param selected Draws the `chip on` state. Ignored while [onClick] is null.
 * @param badge The wireframe's trailing `FLEET` marker on a temporarily-assigned vehicle (US-13.9).
 */
@Composable
internal fun VehicleChip(
    label: String,
    dot: Color,
    modifier: Modifier = Modifier,
    selected: Boolean = false,
    onClick: (() -> Unit)? = null,
    badge: (@Composable () -> Unit)? = null,
) {
    val shape = RoundedCornerShape(MageRideTheme.radius.lg)
    val chosen = selected && onClick != null

    Row(
        modifier = modifier
            .background(
                color = if (chosen) {
                    MaterialTheme.colorScheme.primaryContainer
                } else {
                    MaterialTheme.colorScheme.surfaceVariant
                },
                shape = shape,
            )
            .then(
                if (chosen) {
                    Modifier.border(ControlTokens.BorderSelected, MaterialTheme.colorScheme.primary, shape)
                } else {
                    Modifier
                },
            )
            .then(
                // `role = RadioButton`: the selector is a one-of-many choice, and TalkBack should
                // say so rather than announce a row of buttons.
                if (onClick == null) {
                    Modifier
                } else {
                    Modifier.selectable(selected = selected, role = Role.RadioButton, onClick = onClick)
                },
            )
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs, Alignment.CenterHorizontally),
    ) {
        Box(modifier = Modifier.size(ControlTokens.StatusDot).background(dot, CircleShape))
        Text(
            text = label,
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        badge?.invoke()
    }
}

/** How much of the accent a banner keeps. Light enough to read `onSurface` text over. */
private const val BANNER_TINT = 0.14f
