package lk.mageride.passenger.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.Coordinates
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-026a — "Add address", the M3 `ModalBottomSheet` that captures one (AL-26, US-22.2).
 *
 * **Four fields, and the fence says which four**: Address Line 1 (street/building), Address Line 2
 * (area/suburb), Address Line 3 (city/district) and a free-text **Label**. There is no Home/Work
 * control here and no language row — the shortcut was decided by the row that opened the sheet, and
 * SCR-PA-026 is where the pin was placed.
 *
 * **The `Pinned:` caption is the honesty of the whole screen.** It prints the coordinate the row
 * will be saved at, because everything else on the sheet is text the passenger can edit: the lines
 * are a label for a point, and the point is what a booking will actually navigate to.
 *
 * **Delete lives here** (US-22.3). SCR-PA-026 draws a ✎ per row and no ✕; reaching delete through
 * the edit sheet also means the passenger has just been shown which address it is.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AddAddressSheet(
    sheet: AddressSheetState,
    onLabelChanged: (String) -> Unit,
    onLine1Changed: (String) -> Unit,
    onLine2Changed: (String) -> Unit,
    onLine3Changed: (String) -> Unit,
    onSave: () -> Unit,
    onDelete: () -> Unit,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(
                    if (sheet.editing) R.string.address_sheet_edit_title else R.string.address_sheet_add_title,
                ),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            Text(
                // The coordinate is a Kotlin-formatted constant inside translated copy — see
                // `Coordinates`, which is why the digits are not in `strings.xml`.
                text = stringResource(R.string.address_sheet_pinned, Coordinates.format(sheet.point)),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            LabelledTextField(
                label = stringResource(R.string.address_line1),
                value = sheet.line1,
                onValueChange = onLine1Changed,
                enabled = !sheet.saving,
            )
            LabelledTextField(
                label = stringResource(R.string.address_line2),
                value = sheet.line2,
                onValueChange = onLine2Changed,
                enabled = !sheet.saving,
            )
            LabelledTextField(
                label = stringResource(R.string.address_line3),
                value = sheet.line3,
                onValueChange = onLine3Changed,
                enabled = !sheet.saving,
            )
            LabelledTextField(
                label = stringResource(R.string.address_label),
                value = sheet.label,
                onValueChange = onLabelChanged,
                placeholder = stringResource(R.string.address_label_hint),
                enabled = !sheet.saving,
                imeAction = ImeAction.Done,
            )

            MageRideCta(
                label = stringResource(R.string.address_save),
                onClick = onSave,
                enabled = sheet.canSave,
                loading = sheet.saving,
            )

            if (sheet.editing) {
                TextButton(onClick = onDelete, enabled = !sheet.saving) {
                    Text(
                        text = stringResource(R.string.address_delete),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }
}
