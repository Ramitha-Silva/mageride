package lk.mageride.passenger.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.iam.SavedAddress

/**
 * SCR-PA-026 — "Saved addresses".
 *
 * The wireframe, top to bottom: the ★ **Home** and ★ **Work** rows, then the 📍 labelled ones, then
 * the pin map, the *"drop / drag pin"* caption and the `＋ Add address` CTA.
 *
 * **Home and Work are always drawn, set or not.** They are the two rows US-22.1 is about and the
 * wireframe prints them above everything else; a passenger who has saved neither still needs
 * somewhere to tap, and *"Not set"* under the row is that somewhere. The ✎ on either opens
 * SCR-PA-026a with the shortcut already decided — see the view model, which is where the reading of
 * *"save Home & Work by pin"* is argued.
 *
 * **The pin is the map's centre and the map moves under it.** MapLibre's draggable annotations are
 * in a plugin artifact this module cannot take, so the wireframe's *"drop / drag pin"* is a fixed
 * marker over a map that pans — the same picker C079 and SCR-PA-011 use.
 */
@Composable
internal fun SavedAddressesScreen(onBack: () -> Unit, model: SavedAddressesViewModel) {
    val state by model.state.collectAsStateWithLifecycle()
    val homeLabel = stringResource(R.string.addresses_home)
    val workLabel = stringResource(R.string.addresses_work)

    Column(modifier = Modifier.fillMaxSize()) {
        SettingsTopBar(title = stringResource(R.string.drawer_saved_addresses), onBack = onBack)

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            state.error?.let { InlineError(stringResource(it)) }

            if (state.loading) {
                Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(modifier = Modifier.size(ControlTokens.RowIcon))
                }
            }

            ShortcutRow(
                label = homeLabel,
                address = state.home,
                busy = state.busyWith != null && state.busyWith == state.home?.addressId,
                onEdit = { model.editShortcut(AddressShortcut.HOME, homeLabel) },
            )
            ShortcutRow(
                label = workLabel,
                address = state.work,
                busy = state.busyWith != null && state.busyWith == state.work?.addressId,
                onEdit = { model.editShortcut(AddressShortcut.WORK, workLabel) },
            )

            state.labelled.forEach { address ->
                SettingsRow(
                    icon = Icons.Filled.LocationOn,
                    label = address.label,
                    supporting = addressLine(address),
                    onClick = { model.edit(address) },
                    trailing = { EditButton(enabled = state.busyWith != address.addressId) { model.edit(address) } },
                )
            }

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(ControlTokens.InlineMap),
                contentAlignment = Alignment.Center,
            ) {
                MageRideMap(
                    camera = state.pin?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
                    onCameraIdle = model::onPinMoved,
                )
                // The fixed marker, drawn in Compose over the map so it stays exactly at the centre
                // through every gesture. See the screen KDoc.
                Icon(
                    imageVector = Icons.Filled.LocationOn,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ListRowIcon),
                    tint = MaterialTheme.colorScheme.primary,
                )
            }

            Text(
                text = stringResource(R.string.addresses_pin_hint),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            MageRideCta(
                label = stringResource(R.string.addresses_add),
                onClick = model::addAddress,
                // There is nowhere to put an address until the map has settled somewhere, which is
                // one camera-idle away on every device and immediate on one with a fix.
                enabled = state.pin != null,
            )
        }
    }

    state.sheet?.let { sheet ->
        AddAddressSheet(
            sheet = sheet,
            onLabelChanged = model::onLabelChanged,
            onLine1Changed = model::onLine1Changed,
            onLine2Changed = model::onLine2Changed,
            onLine3Changed = model::onLine3Changed,
            onSave = model::save,
            onDelete = model::delete,
            onDismiss = model::dismissSheet,
        )
    }
}

/**
 * The ★ Home or ★ Work row.
 *
 * An unset shortcut still draws its row — see the screen KDoc — with *"Not set"* where the address
 * would be, so the ✎ has something to sit beside.
 */
@Composable
private fun ShortcutRow(label: String, address: SavedAddress?, busy: Boolean, onEdit: () -> Unit) {
    SettingsRow(
        icon = Icons.Filled.Star,
        label = label,
        supporting = address?.let(::addressLine) ?: stringResource(R.string.addresses_not_set),
        onClick = onEdit,
        trailing = { EditButton(enabled = !busy, onClick = onEdit) },
    )
}

@Composable
private fun EditButton(enabled: Boolean, onClick: () -> Unit) {
    IconButton(onClick = onClick, enabled = enabled) {
        Icon(
            imageVector = Icons.Filled.Edit,
            contentDescription = stringResource(R.string.addresses_edit),
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * `221 Galle Rd, Dehiwala` — the stored lines, on one line.
 *
 * The three AL-26 lines joined by the wireframe's own `, `, empties dropped. A row is one line of
 * supporting text; the whole address is what SCR-PA-026a shows, field by field.
 */
private fun addressLine(address: SavedAddress): String = listOfNotNull(address.line1, address.line2, address.line3)
    .filter(String::isNotBlank)
    .joinToString(LINE_SEPARATOR)

/** The wireframe's `, ` between address lines. Punctuation, not copy. */
private const val LINE_SEPARATOR = ", "
