package lk.mageride.passenger.onboarding

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-004 — the first profile.
 *
 * The wireframe's four rows: the avatar with its `＋` badge, Full name, Language, and the
 * *"Notifications & offers"* switch, over a pinned *"Save & continue"*.
 *
 * **The photo picker is not wired, and the badge says so by doing nothing yet.** D2' names
 * `PhotosPicker`/`AsyncImage` and an avatar crop sheet; `UpdateProfileRequest.photoUrl` is a
 * *URL*, and nothing on the app-facing surface mints one for a passenger — `POST /v1/support/screenshots`
 * and the driver's document routes are the only uploads in the contract set, and neither is this.
 * The field is optional in the contract and the wireframe's own state line calls the name the
 * required one. Recorded in the C077 handoff; landing it needs an upload route first.
 *
 * @param onSaved Move to SCR-PA-005.
 */
@Composable
internal fun ProfileSetupScreen(
    onSaved: () -> Unit,
    onBack: () -> Unit,
    model: ProfileSetupViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    val saved by model.saved.collectAsStateWithLifecycle()

    LaunchedEffect(saved) {
        if (saved) onSaved()
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.action_back),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = stringResource(R.string.profile_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
        ) {
            Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
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
            }

            LabelledTextField(
                label = stringResource(R.string.profile_name_label),
                value = state.name,
                onValueChange = model::onNameChanged,
                enabled = state.loaded && !state.busy,
                isError = state.error != null,
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Done,
            )

            LanguageField(
                selected = state.language,
                enabled = state.loaded && !state.busy,
                onSelect = model::onLanguageChanged,
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.profile_notifications),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Box(modifier = Modifier.weight(1f))
                Switch(
                    checked = state.notificationsEnabled,
                    onCheckedChange = model::onNotificationsChanged,
                    enabled = state.loaded && !state.busy,
                )
            }

            state.error?.let { InlineError(stringResource(it)) }

            Box(modifier = Modifier.weight(1f))

            MageRideCta(
                label = stringResource(R.string.profile_save),
                onClick = model::submit,
                enabled = state.canSubmit,
                loading = state.busy,
                modifier = Modifier.padding(bottom = MageRideTheme.spacing.md),
            )
        }
    }
}

/**
 * The wireframe's Language row — a read-only field with a `▾`.
 *
 * `ExposedDropdownMenuBox` rather than the vertical boxes SCR-PA-002 uses: there the choice is the
 * screen's whole purpose and deserves the room, here it is one row of four that is already
 * answered. D2' §SCR-PA-004 names `ExposedDropdownMenu` for exactly this.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun LanguageField(
    selected: lk.mageride.shared.data.models.Language,
    enabled: Boolean,
    onSelect: (lk.mageride.shared.data.models.Language) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
        SectionLabel(stringResource(R.string.profile_language_label))
        ExposedDropdownMenuBox(
            expanded = expanded,
            onExpandedChange = { if (enabled) expanded = it },
        ) {
            OutlinedTextField(
                value = selected.endonym,
                onValueChange = {},
                readOnly = true,
                enabled = enabled,
                singleLine = true,
                trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
                modifier = Modifier
                    .fillMaxWidth()
                    // `menuAnchor()` with no argument is deprecated in Material3 1.4, and the type
                    // is spelled `ExposedDropdownMenuAnchorType` there — `MenuAnchorType` is the
                    // 1.3 name and does not resolve against this BOM.
                    .menuAnchor(androidx.compose.material3.ExposedDropdownMenuAnchorType.PrimaryNotEditable),
            )
            ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                LanguageChoices.forEach { language ->
                    DropdownMenuItem(
                        text = { Text(language.endonym) },
                        onClick = {
                            onSelect(language)
                            expanded = false
                        },
                    )
                }
            }
        }
    }
}
