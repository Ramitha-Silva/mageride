package lk.mageride.passenger.ui.component

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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/** The wireframe's `t-label` — the caption above a group of controls. */
@Composable
internal fun SectionLabel(text: String, modifier: Modifier = Modifier, centred: Boolean = false) {
    Text(
        text = text,
        modifier = modifier.fillMaxWidth(),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = if (centred) TextAlign.Center else TextAlign.Start,
    )
}

/**
 * SCR-PA-002's language box — the wireframe's centred `field`, one per row.
 *
 * **US-1.3 is explicit about the shape**: *"vertical selectable boxes, one per row, ordered
 * Sinhala (first) → Tamil → English (default highlight = Sinhala)"*. D2' §SCR-PA-002's own sketch
 * still draws a `SegmentedButton` — the wireframe supersedes it, and the prompt's fence says so;
 * recorded in the C077 handoff.
 *
 * Selected is a thicker primary border over a primary-container tint, unselected a hairline
 * outline. `selectable(role = Role.RadioButton)` puts the whole box into the accessibility tree as
 * one choice, so TalkBack reads "සිංහල · Sinhala, selected" rather than announcing a control and
 * a label separately.
 *
 * @param label The endonym — `සිංහල`. A proper noun, so it is a Kotlin constant and not a string
 *   resource: three identical values across the three files is what `StringResourceTest` reads as
 *   a translation nobody did.
 * @param secondary The English name beside it, absent on the English row itself.
 */
@Composable
internal fun SelectionBox(
    label: String,
    selected: Boolean,
    onSelect: () -> Unit,
    modifier: Modifier = Modifier,
    secondary: String? = null,
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
            .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs, Alignment.CenterHorizontally),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        if (secondary != null) {
            Text(
                text = "· $secondary",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/**
 * The carousel's paging dots — the current slide is a filled bar, the rest are outline circles.
 *
 * The wireframe's `.dots i.on` is wider than tall (`width:18px;border-radius:4px`), which is what
 * makes the current slide readable at a glance rather than a matter of shade.
 */
@Composable
internal fun PagerDots(count: Int, current: Int, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        repeat(count) { index ->
            val active = index == current
            Box(
                modifier = Modifier
                    .width(if (active) ControlTokens.DotActive else ControlTokens.Dot)
                    .height(ControlTokens.Dot)
                    .background(
                        color = if (active) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.outline
                        },
                        shape = if (active) {
                            RoundedCornerShape(MageRideTheme.radius.sm)
                        } else {
                            CircleShape
                        },
                    ),
            )
        }
    }
}

/**
 * The wireframe's `illus` block — a tinted panel standing in for an illustration.
 *
 * **content-svc serves an illustration REFERENCE, never image bytes.** `OnboardingSlide
 * .illustrationRef` is *"an app-bundled asset key (`onboarding/driver-wallet`), or an absolute
 * https URL when the deployment sets an asset base"*, and this module ships no image loader — no
 * Coil, no Glide — so a remote reference has nothing to render it. Drawing the slide from the icon
 * set already in the app is the honest stand-in, and the caption below it is the slide's own
 * trilingual copy, which *is* content-svc's. See the C077 handoff.
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

/**
 * An inline error line under a field or above a CTA.
 *
 * The wireframe's red hint. D-26: the copy is always a resolved string resource — never a
 * `ProblemDetails.title`, which is English prose written for an operator. See `OnboardingErrors`.
 */
@Composable
internal fun InlineError(message: String, modifier: Modifier = Modifier) {
    Text(
        text = message,
        modifier = modifier.fillMaxWidth(),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.error,
    )
}
