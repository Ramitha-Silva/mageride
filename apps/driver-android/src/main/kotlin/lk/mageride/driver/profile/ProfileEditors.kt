package lk.mageride.driver.profile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Contacts
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.onboarding.PhoneNumber
import lk.mageride.driver.onboarding.endonym
import lk.mageride.driver.onboarding.englishName
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.PhoneNumberField
import lk.mageride.driver.ui.component.SelectionBox
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Language

/**
 * SCR-DA-029's three editors, each a modal bottom sheet.
 *
 * A sheet rather than a destination for each: none of the three is drawn as a screen in the
 * wireframe — the ✎ on the header row and the ✎ on the emergency-contact row are affordances *on*
 * the profile, and AL-35's ruling on SCR-DA-030's rating sheet ("a modal bottom sheet, not an
 * inline card") is the same shape applied to the same kind of edit.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ProfileEditorSheet(
    state: DriverProfileState,
    viewModel: DriverProfileViewModel,
    modifier: Modifier = Modifier,
) {
    val sheet = state.sheet ?: return

    ModalBottomSheet(
        onDismissRequest = viewModel::dismissSheet,
        modifier = modifier,
        sheetState = rememberModalBottomSheetState(),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(
                    start = MageRideTheme.spacing.md,
                    end = MageRideTheme.spacing.md,
                    bottom = MageRideTheme.spacing.lg,
                ),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            when (sheet) {
                ProfileSheet.Name -> NameEditor(state = state, viewModel = viewModel)
                ProfileSheet.Emergency -> EmergencyContactEditor(state = state, viewModel = viewModel)
                ProfileSheet.Language -> LanguageEditor(state = state, viewModel = viewModel)
            }
        }
    }
}

/** The header ✎ — `PUT /v1/users/me` with a display name (US-1.5). */
@Composable
private fun NameEditor(state: DriverProfileState, viewModel: DriverProfileViewModel) {
    Text(
        text = stringResource(R.string.profile_name_title),
        style = MaterialTheme.typography.titleMedium,
        color = MaterialTheme.colorScheme.onSurface,
    )
    LabelledTextField(
        label = stringResource(R.string.profile_name_label),
        value = state.nameDraft,
        onValueChange = viewModel::onNameChange,
    )
    MageRideCta(
        label = stringResource(R.string.action_save),
        onClick = viewModel::saveName,
        enabled = state.canSaveName,
        loading = state.saving,
    )
}

/**
 * AL-13's emergency contact — the name and number **SCR-DA-032's driver SOS sends to** (D-33).
 *
 * The picker is offered above the fields rather than instead of them: an address book is the fast
 * path and typing is the one that always works, and safety-svc answers
 * `400 no-emergency-contact` to an SOS raised by an account with none.
 */
@Composable
private fun EmergencyContactEditor(state: DriverProfileState, viewModel: DriverProfileViewModel) {
    val pickContact = rememberContactPicker { name, phone ->
        viewModel.onContactPicked(name = name, phone = phone)
    }

    Text(
        text = stringResource(R.string.profile_emergency_title),
        style = MaterialTheme.typography.titleMedium,
        color = MaterialTheme.colorScheme.onSurface,
    )
    Text(
        text = stringResource(R.string.profile_emergency_why),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    TextButton(onClick = pickContact) {
        Icon(
            imageVector = Icons.Outlined.Contacts,
            contentDescription = null,
            modifier = Modifier
                .size(ControlTokens.RowIcon)
                .padding(end = MageRideTheme.spacing.xxs),
        )
        Text(text = stringResource(R.string.profile_emergency_pick))
    }
    LabelledTextField(
        label = stringResource(R.string.profile_emergency_name_label),
        value = state.contactNameDraft,
        onValueChange = viewModel::onContactNameChange,
    )
    PhoneNumberField(
        value = state.contactPhoneDraft,
        onValueChange = viewModel::onContactPhoneChange,
        isError = state.contactPhoneRejected,
        supporting = stringResource(R.string.profile_emergency_phone_invalid)
            .takeIf { state.contactPhoneRejected },
        placeholder = PhoneNumber.PLACEHOLDER,
        prefix = PhoneNumber.COUNTRY_CODE,
    )
    MageRideCta(
        label = stringResource(R.string.action_save),
        onClick = viewModel::saveEmergencyContact,
        enabled = state.canSaveContact,
        loading = state.saving,
    )
}

/** AL-26's three boxes again — the same control SCR-DA-002 uses, and the same endonym table. */
@Composable
private fun LanguageEditor(state: DriverProfileState, viewModel: DriverProfileViewModel) {
    Text(
        text = stringResource(R.string.profile_language_title),
        style = MaterialTheme.typography.titleMedium,
        color = MaterialTheme.colorScheme.onSurface,
    )
    Language.entries.forEach { language ->
        SelectionBox(
            label = language.endonym,
            selected = state.profile?.language == language,
            onSelect = { viewModel.chooseLanguage(language) },
            secondary = language.englishName,
        )
    }
}
