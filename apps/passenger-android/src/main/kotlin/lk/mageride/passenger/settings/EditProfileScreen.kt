package lk.mageride.passenger.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.onboarding.PhoneNumber
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.component.PhoneNumberField
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.iam.EmergencyContact

/**
 * SCR-PA-027b — "Edit profile".
 *
 * The wireframe: `‹ Edit profile … Save`, the avatar with its 📷 badge and *"Take photo or upload"*,
 * **Full name**, the **Notifications & offers** switch, and the SOS contact list over
 * `＋ Add SOS contact`.
 *
 * **No language row** (AL-26) — the wireframe says so, and the view model's save has no field for
 * one.
 *
 * **The photo control is drawn and inert, and that is a missing route rather than a missing
 * screen.** `UpdateProfileRequest.photoUrl` is a *URL*, and nothing on the app-facing surface mints
 * one for a passenger: `POST /v1/support/screenshots`, `POST /v1/mode-b/payments/{id}/transfer-slip`
 * and the driver's document uploads are the whole upload set, and none of them is an avatar. C077
 * hit the same wall on SCR-PA-004 and made the same call; landing it needs an upload route first.
 * Disabled rather than silently ignored, because a control that looks live and does nothing is
 * worse than one that says it is not ready. Recorded in the C083 handoff.
 */
@Composable
@Suppress("LongMethod") // The screen is the wireframe's layout tree, top to bottom.
internal fun EditProfileScreen(onBack: () -> Unit, model: EditProfileViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.saved) {
        if (state.saved) onBack()
    }

    Column(modifier = Modifier.fillMaxSize()) {
        SettingsTopBar(
            title = stringResource(R.string.edit_profile_title),
            onBack = onBack,
            actions = {
                MageRideTextLink(
                    label = stringResource(R.string.action_save),
                    onClick = model::save,
                    enabled = state.canSave,
                )
            },
        )

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            state.error?.let { InlineError(stringResource(it)) }

            AvatarEditor()

            LabelledTextField(
                label = stringResource(R.string.edit_profile_name),
                value = state.name,
                onValueChange = model::onNameChanged,
                enabled = state.loaded && !state.saving,
                imeAction = ImeAction.Done,
            )

            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = stringResource(R.string.edit_profile_notifications),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Switch(
                    checked = state.notificationsEnabled,
                    onCheckedChange = model::onNotificationsChanged,
                    enabled = state.loaded && !state.saving,
                )
            }

            SectionLabel(
                text = stringResource(R.string.edit_profile_sos_contacts),
                modifier = Modifier.padding(top = MageRideTheme.spacing.xxs),
            )

            if (state.contacts.isEmpty()) {
                // What an empty list costs, said here rather than at the moment of an alarm:
                // `POST /v1/sos` answers `400 no-emergency-contact` with none on file.
                Text(
                    text = stringResource(R.string.edit_profile_sos_empty),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            state.contacts.forEach { contact ->
                ContactRow(
                    contact = contact,
                    busy = state.removing == contact.contactId,
                    onEdit = { model.editContact(contact) },
                    onRemove = { model.removeContact(contact) },
                )
            }

            OutlinedButton(
                onClick = model::addContact,
                modifier = Modifier.fillMaxWidth(),
                enabled = state.loaded,
            ) {
                Text(stringResource(R.string.edit_profile_add_sos))
            }
        }
    }

    state.contactDraft?.let { draft ->
        ContactDialog(
            draft = draft,
            onNameChanged = model::onContactNameChanged,
            onPhoneChanged = model::onContactPhoneChanged,
            onSave = model::saveContact,
            onDismiss = model::dismissContact,
        )
    }
}

/**
 * The wireframe's `avatar lg` with its 📷 badge and *"Take photo or upload"* textlink.
 *
 * Both are inert — see the screen's KDoc. The photo itself is not rendered either: the profile
 * carries a URL and this app ships no image loader (C082 argued the same point about the owner's
 * LankaQR image and decoded bytes by hand rather than adding one).
 */
@Composable
private fun AvatarEditor() {
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Box(contentAlignment = Alignment.BottomEnd) {
            Box(
                modifier = Modifier
                    .size(ControlTokens.AvatarLarge)
                    .background(MaterialTheme.colorScheme.surfaceVariant, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Filled.Person,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.IllustrationIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Box(
                modifier = Modifier
                    .size(ControlTokens.AvatarBadge)
                    .background(MaterialTheme.colorScheme.surface, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Filled.PhotoCamera,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ChipIcon),
                    tint = MaterialTheme.colorScheme.outlineVariant,
                )
            }
        }

        MageRideTextLink(
            label = stringResource(R.string.edit_profile_photo),
            onClick = {},
            enabled = false,
        )
    }
}

/** One SOS contact — the wireframe's `👤 Amma · +94 77 000 1111 ✎`, plus the delete US-12.1 needs. */
@Composable
private fun ContactRow(contact: EmergencyContact, busy: Boolean, onEdit: () -> Unit, onRemove: () -> Unit) {
    SettingsRow(
        icon = Icons.Filled.Person,
        label = contact.name,
        supporting = contact.phone,
        onClick = onEdit,
        trailing = {
            IconButton(onClick = onEdit, enabled = !busy) {
                Icon(
                    imageVector = Icons.Filled.Edit,
                    contentDescription = stringResource(R.string.edit_profile_edit_sos),
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            IconButton(onClick = onRemove, enabled = !busy) {
                Icon(
                    imageVector = Icons.Filled.Delete,
                    contentDescription = stringResource(R.string.edit_profile_remove_sos),
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.error,
                )
            }
        },
    )
}

/**
 * The add/edit contact dialog.
 *
 * The number is entered through the same `+94`-prefixed field SCR-PA-003 uses, because it is the
 * same question: Sri Lanka is the only country this platform operates in, so the prefix is a label
 * on the field rather than a country picker, and both `0771234567` and `771234567` have to work.
 */
@Composable
private fun ContactDialog(
    draft: SosContactDraft,
    onNameChanged: (String) -> Unit,
    onPhoneChanged: (String) -> Unit,
    onSave: () -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                stringResource(
                    if (draft.contactId == null) R.string.edit_profile_add_sos else R.string.edit_profile_edit_sos,
                ),
            )
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                LabelledTextField(
                    label = stringResource(R.string.edit_profile_sos_name),
                    value = draft.name,
                    onValueChange = onNameChanged,
                    enabled = !draft.saving,
                    keyboardType = KeyboardType.Text,
                )
                SectionLabel(stringResource(R.string.edit_profile_sos_phone))
                PhoneNumberField(
                    value = draft.phone,
                    onValueChange = onPhoneChanged,
                    countryCode = PhoneNumber.COUNTRY_CODE,
                    placeholder = PhoneNumber.PLACEHOLDER,
                    enabled = !draft.saving,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onSave, enabled = draft.canSave) {
                Text(stringResource(R.string.action_save))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}
