package lk.mageride.passenger.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.filled.CreditCard
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.onboarding.LanguageChoices
import lk.mageride.passenger.onboarding.endonym
import lk.mageride.passenger.onboarding.englishName
import lk.mageride.passenger.ride.PaymentRails
import lk.mageride.passenger.shell.findActivity
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.component.SelectionBox
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * SCR-PA-027 — "Profile & settings".
 *
 * The wireframe's eight rows, in its order: the profile card, 🌐 Language, ★ Save Home & Work, 📍
 * Saved addresses, 💳 Default payment, 🔔 Notifications, 💬 Help & support, and the Log out /
 * Delete account pair.
 *
 * **Save Home & Work and Saved addresses both open SCR-PA-026, and that is the wireframe's own
 * doing.** They are two rows onto one screen because they are two intentions — *"set my Home"* and
 * *"manage my addresses"* — and SCR-PA-026 draws the Home and Work rows at the top precisely so the
 * first of them lands somewhere useful.
 *
 * **Default payment offers Cash and Wallet.** The wireframe draws Cash / LankaQR / OnePay, which
 * predates the 2026-08-01 payment-custody change set: AL-57 and AL-59 retired both of those rails,
 * and the third surviving one — the driver's QR — is a settlement choice the contract itself
 * excludes from a stored preference. See `PaymentRails.PREFERABLE`; the wireframe needs a
 * micro-change-set, recorded in the C083 handoff.
 */
@Composable
@Suppress("LongMethod") // The screen is the wireframe's row list, in the wireframe's order.
internal fun SettingsScreen(
    onBack: () -> Unit,
    onEditProfile: () -> Unit,
    onSavedAddresses: () -> Unit,
    onSupport: () -> Unit,
    model: SettingsViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    // A language change has to re-inflate resources, and only the Activity can — see the view
    // model. This is the one screen in the app that recreates its host.
    LaunchedEffect(state.relaunch) {
        if (state.relaunch) {
            model.onRelaunchConsumed()
            context.findActivity()?.recreate()
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        SettingsTopBar(title = stringResource(R.string.drawer_settings), onBack = onBack)

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            state.error?.let { InlineError(stringResource(it)) }

            ProfileCard(profile = state.profile, onClick = onEditProfile)

            SettingsRow(
                icon = Icons.Filled.Language,
                label = stringResource(R.string.settings_language),
                onClick = { model.openPicker(SettingsPicker.LANGUAGE) },
                trailing = { RowChevron(state.language.endonym) },
            )

            SettingsRow(
                icon = Icons.Filled.Star,
                label = stringResource(R.string.settings_save_home_work),
                onClick = onSavedAddresses,
            )

            SettingsRow(
                icon = Icons.Filled.LocationOn,
                label = stringResource(R.string.drawer_saved_addresses),
                onClick = onSavedAddresses,
            )

            SettingsRow(
                icon = Icons.Filled.CreditCard,
                label = stringResource(R.string.settings_default_payment),
                onClick = { model.openPicker(SettingsPicker.PAYMENT) },
                trailing = { RowChevron(stringResource(PaymentRails.label(state.defaultPayment))) },
            )

            SettingsRow(
                icon = Icons.Filled.Notifications,
                label = stringResource(R.string.settings_notifications),
                trailing = {
                    Switch(
                        checked = state.notificationsEnabled,
                        onCheckedChange = model::setNotifications,
                        enabled = !state.loading,
                    )
                },
            )

            SettingsRow(
                icon = Icons.AutoMirrored.Filled.Chat,
                label = stringResource(R.string.drawer_support),
                onClick = onSupport,
            )

            // The 202's acknowledgement, which is deliberately NOT "your account has been deleted".
            if (state.deletionRequestId != null) {
                Text(
                    text = stringResource(R.string.settings_delete_requested),
                    modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = MageRideTheme.spacing.xxs),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                MageRideTextLink(label = stringResource(R.string.drawer_log_out), onClick = model::logOut)
                TextButton(onClick = model::confirmDelete, enabled = state.deletionRequestId == null) {
                    Text(
                        text = stringResource(R.string.settings_delete_account),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }

    when (state.picker) {
        SettingsPicker.LANGUAGE -> LanguageDialog(
            selected = state.language,
            onSelect = model::chooseLanguage,
            onDismiss = model::dismissPicker,
        )

        SettingsPicker.PAYMENT -> PaymentDialog(
            selected = state.defaultPayment,
            onSelect = model::chooseDefaultPayment,
            onDismiss = model::dismissPicker,
        )

        null -> Unit
    }

    if (state.confirmingDelete) {
        DeleteAccountDialog(onConfirm = model::deleteAccount, onDismiss = model::dismissDelete)
    }
}

/**
 * The wireframe's `row card` — avatar, name, `ID: … · +94 …`, and the ✎ that opens SCR-PA-027b.
 *
 * **The id is the account's ULID.** The wireframe prints `PAX-90431`, and no contract carries a
 * human-readable passenger number: `UserProfile` has `userId` and nothing else that identifies the
 * account to its owner. Drawing the ULID is the honest answer — it is what support would ask for —
 * and a made-up `PAX-` prefix over it would be a client inventing an identifier. Recorded in the
 * C083 handoff.
 */
@Composable
private fun ProfileCard(profile: UserProfile?, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .clickable(onClick = onClick)
            .padding(MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Box(
            modifier = Modifier
                .size(ControlTokens.Avatar)
                .background(MaterialTheme.colorScheme.background, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.Person,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = profile?.firstName?.takeIf(String::isNotBlank)
                    ?: stringResource(R.string.settings_unnamed_passenger),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = profile?.let { stringResource(R.string.settings_identity, it.userId, it.phone) }.orEmpty(),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        Icon(
            imageVector = Icons.Filled.Edit,
            contentDescription = stringResource(R.string.settings_edit_profile),
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * The 🌐 row's chooser — the same three boxes SCR-PA-002 draws, in US-1.3's order.
 *
 * `SelectionBox` rather than a plain `DropdownMenu`, because this is the second of the two places
 * AL-26 leaves the language on and a passenger who set it wrong at first run is coming back to the
 * control they remember.
 */
@Composable
private fun LanguageDialog(selected: Language, onSelect: (Language) -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.settings_language)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                LanguageChoices.forEach { language ->
                    SelectionBox(
                        label = language.endonym,
                        secondary = language.englishName,
                        selected = language == selected,
                        onSelect = { onSelect(language) },
                    )
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}

/** The 💳 row's chooser — `PaymentRails.PREFERABLE`, with each rail's own one-line caption. */
@Composable
private fun PaymentDialog(selected: PaymentMethod, onSelect: (PaymentMethod) -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.settings_default_payment)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                PaymentRails.PREFERABLE.forEach { method ->
                    SelectionBox(
                        label = stringResource(PaymentRails.label(method)),
                        secondary = stringResource(PaymentRails.caption(method)),
                        selected = method == selected,
                        onSelect = { onSelect(method) },
                    )
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}

/**
 * US-1.8's confirm dialog, and the sentence that keeps it honest.
 *
 * The request is **accepted**, not executed: pdpa-svc may hold it under a statutory retention rule
 * (E-06), so the dialog promises a request rather than a deletion. Saying "your account will be
 * deleted" and then holding the data for seven years is the PDPA claim this app must not make.
 */
@Composable
private fun DeleteAccountDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.settings_delete_title)) },
        text = { Text(stringResource(R.string.settings_delete_body)) },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(
                    text = stringResource(R.string.settings_delete_confirm),
                    color = MaterialTheme.colorScheme.error,
                )
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}
