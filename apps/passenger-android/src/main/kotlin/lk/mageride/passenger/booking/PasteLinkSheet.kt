package lk.mageride.passenger.booking

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.Coordinates
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Place
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-012a — paste a Google Maps link, get a pin.
 *
 * The wireframe: a title naming **which** field is being filled (`📦 DROP-OFF`), the explanatory
 * line about a link from WhatsApp, the 📋 **Paste** button, and then whichever of the four states
 * applies — Empty, Parsing, Resolved (pin preview + address + coordinates + *"Use this location"*)
 * or Error (*"couldn't read that link — pick on map"*).
 *
 * **Opened from three places** — SCR-PA-010b's proxy pickup and SCR-PA-012's package pickup and
 * drop-off — which is why the caller supplies both the label and what *"pick on map"* does.
 *
 * @param label Which field this is filling. The wireframe's `📦 DROP-OFF` chip.
 * @param onUse The resolved place. The sheet closes on it.
 * @param onPickOnMap The Error state's way out, and the reason an unreadable link is not a dead end.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PasteLinkSheet(
    label: String,
    onUse: (Place) -> Unit,
    onPickOnMap: () -> Unit,
    onDismiss: () -> Unit,
    model: PasteLinkViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    val clipboard = LocalClipboardManager.current

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text(
                    text = stringResource(R.string.paste_title),
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = label,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Text(
                text = stringResource(R.string.paste_explainer),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            OutlinedButton(
                // The clipboard is read on the tap, never on open. Android 12+ shows a toast every
                // time an app reads the clipboard unprompted, and a sheet that harvested it on
                // appear would accuse itself in front of the passenger.
                onClick = { model.onPasted(clipboard.getText()?.text.orEmpty()) },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(
                    imageVector = Icons.Filled.ContentPaste,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                )
                Text(
                    text = stringResource(R.string.paste_action),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xs),
                )
            }

            when (val current = state) {
                PasteLinkState.Empty -> Unit

                PasteLinkState.Parsing -> Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    CircularProgressIndicator(modifier = Modifier.size(ControlTokens.RowIcon))
                    Text(
                        text = stringResource(R.string.paste_reading),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                is PasteLinkState.Resolved -> ResolvedPreview(current, onUse = { onUse(current.asPlace()) })

                PasteLinkState.Error -> Column(
                    verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                ) {
                    Text(
                        text = stringResource(R.string.paste_unreadable),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                    OutlinedButton(onClick = onPickOnMap, modifier = Modifier.fillMaxWidth()) {
                        Text(stringResource(R.string.search_select_on_map))
                    }
                }
            }
        }
    }
}

/**
 * The Resolved state.
 *
 * Shows the coordinates **as well as** the address, because the address is a reverse-geocode of a
 * point somebody else chose and can be a street away — the numbers are the thing that was actually
 * resolved, and a sender checking a link against a screenshot reads them.
 */
@Composable
private fun ResolvedPreview(state: PasteLinkState.Resolved, onUse: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
        SectionLabel(text = stringResource(R.string.paste_resolved))

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Icon(
                imageVector = Icons.Filled.LocationOn,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.primary,
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = state.address ?: stringResource(R.string.paste_naming),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = Coordinates.format(state.point),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        MageRideCta(label = stringResource(R.string.paste_use), onClick = onUse)
    }
}
