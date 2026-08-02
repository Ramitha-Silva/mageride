package lk.mageride.driver.ui.component

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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/** The wireframe's `t-label` — the caption above a group of controls. */
@Composable
internal fun SectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        modifier = modifier.fillMaxWidth(),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

/**
 * The wireframe's bordered `listrow` with a radio on the right — the shape SCR-DA-002 uses for
 * **both** of its selectors.
 *
 * AL-26 replaced the dropdown with **vertical boxes**, and the city list is a radio list of the
 * same box. One composable so the two cannot drift: selected is a thicker primary border over a
 * primary-container tint, unselected a hairline outline.
 *
 * `selectable(role = Role.RadioButton)` puts the whole box into the accessibility tree as one
 * choice, so TalkBack reads "සිංහල, selected" instead of announcing a radio and a label apart.
 *
 * @param secondary The English name beside a native one — the wireframe's `සිංහල  Sinhala`.
 * @param leading An optional icon before the label; the 📍 on a city row.
 */
@Composable
internal fun SelectionBox(
    label: String,
    selected: Boolean,
    onSelect: () -> Unit,
    modifier: Modifier = Modifier,
    secondary: String? = null,
    leading: ImageVector? = null,
) {
    val shape = RoundedCornerShape(MageRideTheme.radius.md)
    Row(
        modifier = modifier
            .fillMaxWidth()
            .border(
                width = if (selected) ControlTokens.BorderSelected else ControlTokens.Border,
                color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
                shape = shape,
            )
            .background(
                color = if (selected) MaterialTheme.colorScheme.primaryContainer else Color.Transparent,
                shape = shape,
            )
            .selectable(selected = selected, role = Role.RadioButton, onClick = onSelect)
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        if (leading != null) {
            Icon(
                imageVector = leading,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            text = label,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        if (secondary != null) {
            Text(
                text = secondary,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Box(modifier = Modifier.weight(1f))
        // `onClick = null` — the row owns the click; a second target would double-announce it.
        RadioButton(selected = selected, onClick = null)
    }
}

/** The carousel's paging dots — filled for the current slide, outlined for the rest. */
@Composable
internal fun PagerDots(count: Int, current: Int, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        repeat(count) { index ->
            val active = index == current
            Box(
                modifier = Modifier
                    .size(if (active) ControlTokens.DotActive else ControlTokens.Dot)
                    .background(
                        color = if (active) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.outline
                        },
                        shape = CircleShape,
                    ),
            )
        }
    }
}

/**
 * The wireframe's `illus` block — a tinted panel standing in for an illustration.
 *
 * **AL-28 has content-svc serving the carousel's illustrations, and no route serves them.**
 * `content.yaml` carries the operating cities, the notification templates and the broadcasts, and
 * nothing else; BR-25.1's slide assets have no endpoint. Drawing the slide from the icon set
 * already in the app is the honest stand-in — see the C068 handoff, where this is raised as a
 * micro-change-set.
 */
@Composable
internal fun IllustrationPanel(icon: ImageVector, caption: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .height(ControlTokens.IllustrationPanel)
            .background(
                color = MaterialTheme.colorScheme.surfaceVariant,
                shape = RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(MageRideTheme.spacing.xs),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.IllustrationIcon),
            tint = MaterialTheme.colorScheme.primary,
        )
        Text(
            text = caption,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}
