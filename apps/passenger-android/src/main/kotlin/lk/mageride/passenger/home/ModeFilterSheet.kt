package lk.mageride.passenger.home

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType

/**
 * SCR-PA-006 — the mode and type filter.
 *
 * An M3 `ModalBottomSheet` over the map, exactly as the wireframe draws it: three mode rows with
 * their A/B/C badges, a divider, then the type chips.
 *
 * **Every chip carries its own vehicle icon tinted with its MAP-03 colour**, which the wireframe
 * calls out in as many words — *"not a plain dot … so the chip matches the map marker colour"*. The
 * colour comes from the same `VehicleColors.Legend` the `SymbolLayer` tints with, so the two cannot
 * drift.
 *
 * **Apply closes the sheet and nothing else.** The filter is *"instant client-side"* (the wireframe
 * again) — every toggle has already redrawn the map underneath, so the CTA is a dismiss, not a
 * commit. Making it a commit would mean a passenger could not see what they were choosing.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ModeFilterSheet(
    filter: MapFilter,
    onModeChanged: (ServiceMode, Boolean) -> Unit,
    onTypeChanged: (VehicleType, Boolean) -> Unit,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.filter_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )

            MapFilter.MODES.forEach { mode ->
                ModeRow(
                    mode = mode,
                    enabled = mode in filter.modes,
                    onChange = { onModeChanged(mode, it) },
                )
            }

            SectionLabel(
                text = stringResource(R.string.filter_vehicle_types),
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )

            FlowRow(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
            ) {
                MapFilter.CHIP_TYPES.forEach { type ->
                    TypeChip(
                        type = type,
                        enabled = type in filter.types,
                        onChange = { onTypeChanged(type, it) },
                    )
                }
            }

            MageRideCta(
                label = stringResource(R.string.filter_apply),
                onClick = onDismiss,
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )
        }
    }
}

/** One mode row — badge, name, switch. */
@Composable
private fun ModeRow(mode: ServiceMode, enabled: Boolean, onChange: (Boolean) -> Unit) {
    val modes = MageRideTheme.mode
    val colour = when (mode) {
        ServiceMode.A -> modes.modeA
        ServiceMode.B -> modes.modeB
        ServiceMode.C -> modes.modeC
    }

    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Box(
            modifier = Modifier
                .size(ControlTokens.ListRowIcon)
                .background(colour, RoundedCornerShape(MageRideTheme.radius.sm)),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = VehicleLabels.modeBadge(mode),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onPrimary,
                textAlign = TextAlign.Center,
            )
        }
        Text(
            text = stringResource(VehicleLabels.modeName(mode)),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Box(modifier = Modifier.weight(1f))
        Switch(checked = enabled, onCheckedChange = onChange)
    }
}

/** One type chip — the wireframe's `chip` with its `vico` swatch. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TypeChip(type: VehicleType, enabled: Boolean, onChange: (Boolean) -> Unit) {
    val tint = VehicleLabels.colour(MageRideTheme.vehicle, type)

    FilterChip(
        selected = enabled,
        onClick = { onChange(!enabled) },
        label = { Text(stringResource(VehicleLabels.name(type))) },
        leadingIcon = {
            // The swatch IS the marker colour — a disc of the legend tint with the type's own
            // glyph on it, so a chip and the pin it controls are recognisably the same thing.
            Box(
                modifier = Modifier
                    .size(ControlTokens.ListRowIcon)
                    .background(tint, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = VehicleLabels.icon(type),
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ChipIcon),
                    tint = MaterialTheme.colorScheme.onPrimary,
                )
            }
        },
        colors = FilterChipDefaults.filterChipColors(
            selectedContainerColor = MaterialTheme.colorScheme.primaryContainer,
            selectedLabelColor = MaterialTheme.colorScheme.onPrimaryContainer,
        ),
    )
}
