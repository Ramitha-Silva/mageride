package lk.mageride.driver.ui.component

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Flag
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * The wireframe's `📷 Tap to capture` tile.
 *
 * Opens **SCR-DA-005**, the shared camera document-scanner (AL-43) — this tile never captures
 * anything itself. A tile whose image is already taken shows a `Done ✓` state rather than the
 * image, because a 3 MB bitmap held for the life of the screen is what makes Profile Setup the
 * screen that gets killed in the background on the handsets this platform is for.
 *
 * @param captured Whether an image is already held for this slot.
 * @param label What the slot is — "front", "back". Always a string resource.
 * @param captureHint The tap affordance, shown while [captured] is false.
 * @param doneLabel The `Done ✓` copy, shown once it is true.
 * @param height The wireframe draws this at three sizes — a pair of 88 dp licence tiles
 *   (SCR-DA-003a), a full-width 120 dp document panel (SCR-DA-004a/b) and a pair of 100 dp vehicle
 *   photo panels (SCR-DA-004c). One composable at three heights, not three composables.
 */
@Suppress("LongParameterList") // A slot's identity, its two captions, its state and its size.
@Composable
internal fun CaptureTile(
    label: String,
    captureHint: String,
    doneLabel: String,
    captured: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    height: Dp = ControlTokens.CaptureTile,
) {
    val shape = RoundedCornerShape(MageRideTheme.radius.md)
    Column(
        modifier = modifier
            .height(height)
            .border(
                width = if (captured) ControlTokens.BorderSelected else ControlTokens.Border,
                color = if (captured) MageRideTheme.status.success else MaterialTheme.colorScheme.outline,
                shape = shape,
            )
            .background(MaterialTheme.colorScheme.surfaceVariant, shape)
            .clickable(onClick = onClick)
            .padding(MageRideTheme.spacing.xs),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(
            imageVector = if (captured) Icons.Outlined.CheckCircle else Icons.Outlined.PhotoCamera,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = if (captured) MageRideTheme.status.success else MaterialTheme.colorScheme.primary,
        )
        Text(
            text = label,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
        Text(
            text = if (captured) doneLabel else captureHint,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * The wireframe's `⚑ Admin verify` chip.
 *
 * AL-29's whole point made visible: a field the driver typed, or one Gemini read with low
 * confidence, is `verify_status='pending'` and sits in the Verification-Officer queue
 * (SCR-AP-003) until an officer confirms it. The driver may carry on — the chip says the value is
 * not trusted yet, not that anything is blocked.
 */
@Composable
internal fun AdminVerifyChip(label: String, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .background(
                color = MageRideTheme.status.warning.copy(alpha = CHIP_TINT),
                shape = RoundedCornerShape(MageRideTheme.radius.sm),
            )
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            imageVector = Icons.Outlined.Flag,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.ChipIcon),
            tint = MageRideTheme.status.warning,
        )
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * The wireframe's `kv` row inside the AI-extract card.
 *
 * @param value What was read, or [emptyLabel] when extraction returned nothing.
 * @param flagLabel The ⚑ chip's copy; `null` hides the chip.
 * @param onEdit `null` for a field the contract has nowhere to send an edit to — see the C068
 *   handoff on `licence_no` / `licence_expiry`.
 */
@Composable
internal fun ExtractedFieldRow(
    label: String,
    value: String?,
    emptyLabel: String,
    modifier: Modifier = Modifier,
    flagLabel: String? = null,
    editLabel: String? = null,
    onEdit: (() -> Unit)? = null,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Box(modifier = Modifier.weight(1f))
        Text(
            text = value?.takeIf(String::isNotBlank) ?: emptyLabel,
            style = MaterialTheme.typography.titleMedium,
            color = if (value.isNullOrBlank()) {
                MaterialTheme.colorScheme.outlineVariant
            } else {
                MaterialTheme.colorScheme.onSurface
            },
        )
        if (flagLabel != null) {
            AdminVerifyChip(label = flagLabel)
        }
        if (onEdit != null && editLabel != null) {
            TextButton(onClick = onEdit) {
                Text(text = editLabel, style = MaterialTheme.typography.labelLarge)
            }
        }
    }
}

/**
 * The wireframe's tinted `card` — used for the AI-extract panel (success) and the manual-entry
 * warning (warning).
 *
 * @param accent The role colour of the left edge and the title; `success` or `warning`.
 */
@Composable
internal fun NoticeCard(
    accent: Color,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    title: String? = null,
    content: @Composable () -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(
            containerColor = accent.copy(alpha = CARD_TINT),
            contentColor = MaterialTheme.colorScheme.onSurface,
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            if (title != null) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (icon != null) {
                        Icon(
                            imageVector = icon,
                            contentDescription = null,
                            modifier = Modifier.size(ControlTokens.RowIcon),
                            tint = accent,
                        )
                    }
                    Text(text = title, style = MaterialTheme.typography.labelSmall, color = accent)
                }
            }
            content()
        }
    }
}

/**
 * What a status pill is saying.
 *
 * [PENDING] is the wireframe's amber `Incomplete · Step 3 of 4`, [DONE] its green `Done ✓` /
 * `Approved`, [NEUTRAL] the plain surface chip a fleet-assigned vehicle carries, and [INFO] the
 * blue `Accepted` on SCR-DA-018.
 *
 * **No tone invents a colour.** D2' §0.2 gives `status` two colours plus the surface, and the
 * wireframe's own `pill-status.info` is `background:var(--secondaryContainer);color:var(--secondary)`
 * — the same `secondary` role C070 already resolved the *"info"* banner to. Added by C072; a fifth
 * tone would need a fifth role, which §0.2 does not have.
 */
internal enum class StatusTone {
    DONE,
    PENDING,
    NEUTRAL,
    INFO,
}

/**
 * The wireframe's `pill-status` — `Done ✓`, `Approved`, `Incomplete · Step 3 of 4`, `FLEET`.
 *
 * One composable rather than one per screen: the same pill appears on three of C069's four screens
 * and on C068's capture tiles, and three sessions drawing it separately is how a design system
 * becomes three design systems.
 */
@Composable
internal fun StatusPill(label: String, tone: StatusTone, modifier: Modifier = Modifier) {
    val accent = when (tone) {
        StatusTone.DONE -> MageRideTheme.status.success
        StatusTone.PENDING -> MageRideTheme.status.warning
        StatusTone.NEUTRAL -> MaterialTheme.colorScheme.onSurfaceVariant
        StatusTone.INFO -> MaterialTheme.colorScheme.secondary
    }
    Text(
        text = label,
        modifier = modifier
            .background(
                color = accent.copy(alpha = CHIP_TINT),
                shape = RoundedCornerShape(MageRideTheme.radius.sm),
            )
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = accent,
    )
}

/**
 * The wireframe's `MODE C` badge in the wizard's app bar.
 *
 * A solid badge in D2' §0.2's mode colour rather than a tinted one: it is an identity, not a
 * status, and it is the one thing on SCR-DA-004 that says which onboarding surface the driver is
 * on — Mode A/B vehicles are the Fleet Portal's (AL-27).
 */
@Composable
internal fun ModeCBadge(label: String, modifier: Modifier = Modifier) {
    Text(
        text = label,
        modifier = modifier
            .background(MageRideTheme.mode.modeC, RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = MageRideTheme.status.onStatus,
    )
}

/** How much of the accent colour a tinted card or chip keeps. Light enough to read text over. */
private const val CARD_TINT = 0.12f
private const val CHIP_TINT = 0.18f
