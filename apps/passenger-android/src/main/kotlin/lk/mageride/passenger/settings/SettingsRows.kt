package lk.mageride.passenger.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * The three settings screens' app bar — the wireframe's `‹ Title` with an optional trailing slot.
 *
 * Three screens draw the same bar and one of them (SCR-PA-027b) puts a `Save` textlink on the
 * right, which is the whole reason this takes a slot rather than being copied twice. A plain `Row`
 * rather than M3's `TopAppBar`: the wireframe's bar is a 16 px title beside a back chevron with no
 * elevation and no scroll behaviour, and `TopAppBar` would bring a title style and insets that the
 * rest of this app's screens do not use.
 */
@Composable
internal fun SettingsTopBar(
    title: String,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
    actions: @Composable RowScope.() -> Unit = {},
) {
    Row(modifier = modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = onBack) {
            Icon(
                imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                contentDescription = stringResource(R.string.action_back),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            text = title,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        actions()
    }
}

/**
 * The wireframe's `.listrow` — a leading glyph, a label, and whatever the row ends with.
 *
 * SCR-PA-026 and SCR-PA-027 are both lists of these and the rows differ only in what sits on the
 * right: a chevron, a value plus a chevron, a switch, or an ✎ button. That is the [trailing] slot;
 * everything else is the same row, which is what keeps two screens written in one session from
 * drifting into two row heights.
 *
 * @param supporting The second line, where the row has one. `null` draws a single-line row.
 * @param onClick `null` makes the row inert — the Notifications row's switch is the control, and a
 *   row that also toggled on tap would fire twice for one thumb.
 */
@Composable
internal fun SettingsRow(
    icon: ImageVector,
    label: String,
    modifier: Modifier = Modifier,
    supporting: String? = null,
    onClick: (() -> Unit)? = null,
    trailing: @Composable RowScope.() -> Unit = { RowChevron() },
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .let { if (onClick == null) it else it.clickable(onClick = onClick) }
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Box(
            modifier = Modifier.size(ControlTokens.ListRowIcon),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = label,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (supporting != null) {
                Text(
                    text = supporting,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        trailing()
    }
}

/** The wireframe's `›`. */
@Composable
internal fun RowChevron(value: String? = null) {
    if (value != null) {
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
    Icon(
        imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
        contentDescription = null,
        modifier = Modifier.size(ControlTokens.RowIcon),
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
