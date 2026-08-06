package lk.mageride.passenger.onboarding

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.component.OtpEntry
import lk.mageride.passenger.ui.component.PhoneNumberField
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-003 — the `+94` number, then the six-digit code.
 *
 * **One screen, two phases.** The wireframe draws the phone field, a divider labelled *"enter
 * code"*, the OTP boxes and the resend line all at once, and that is what this is: the OTP half is
 * disabled until a code is out, so a passenger can see what is coming without being able to type
 * into it early.
 *
 * **Phone-OTP only** (AL-07). There is no Google button and no password field, on this screen or
 * anywhere in this app.
 *
 * @param onSignedIn Where the router decided to go once there is a session.
 * @param onBack The app bar's `‹`. On the OTP phase it returns to the number instead of leaving.
 */
@Composable
internal fun LoginScreen(
    onSignedIn: (PassengerDestination) -> Unit,
    onBack: () -> Unit,
    model: LoginViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    val destination by model.destination.collectAsStateWithLifecycle()

    LaunchedEffect(destination) {
        destination?.let(onSignedIn)
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(
                onClick = { if (state.phase == LoginPhase.OTP) model.editPhoneNumber() else onBack() },
            ) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.action_back),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.login_title),
                style = MaterialTheme.typography.headlineMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.login_subtitle),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            PhoneNumberField(
                value = state.phone,
                onValueChange = model::onPhoneChanged,
                countryCode = PhoneNumber.COUNTRY_CODE,
                placeholder = PhoneNumber.PLACEHOLDER,
                // Locked once a code is out — changing it here would verify against a number the
                // server never sent to. `‹` is the way back, and it cancels the attempt properly.
                enabled = state.phase == LoginPhase.PHONE && !state.busy,
            )

            LabelledDivider(text = stringResource(R.string.login_enter_code))

            OtpEntry(
                value = state.otp,
                onValueChange = model::onOtpChanged,
                length = LoginState.OTP_LENGTH,
                enabled = state.phase == LoginPhase.OTP && !state.busy,
                isError = state.error != null && state.phase == LoginPhase.OTP,
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = stringResource(R.string.login_didnt_get_it),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                MageRideTextLink(
                    // US-1.10's cooldown is shown, not hidden: a bare disabled "Resend" tells a
                    // passenger nothing about why, and they tap it until D-32 locks them out.
                    label = if (state.resendInSeconds > 0) {
                        stringResource(R.string.login_resend_in, state.resendInSeconds)
                    } else {
                        stringResource(R.string.login_resend)
                    },
                    onClick = model::resend,
                    enabled = state.canResend,
                )
            }

            state.attemptsRemaining?.let { remaining ->
                Text(
                    text = stringResource(R.string.login_attempts_remaining, remaining),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            state.error?.let { InlineError(stringResource(it)) }

            Box(modifier = Modifier.weight(1f))

            MageRideCta(
                label = stringResource(R.string.action_continue),
                onClick = model::submit,
                enabled = state.canSubmit,
                loading = state.busy,
                modifier = Modifier.padding(bottom = MageRideTheme.spacing.md),
            )
        }
    }
}

/** The wireframe's `divider` — a hairline with a caption in the middle. */
@Composable
private fun LabelledDivider(text: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        HorizontalDivider(modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.outline)
        Text(
            text = text,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.outlineVariant,
        )
        HorizontalDivider(modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.outline)
    }
}
