package lk.mageride.driver.onboarding

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.OtpEntry
import lk.mageride.driver.ui.component.OtpProgress
import lk.mageride.driver.ui.component.PhoneNumberField
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-003 · phone + OTP** — the whole of sign-in, on one screen.
 *
 * The wireframe draws the number field and the six code cells together, separated by an "enter
 * code" divider, with the resend countdown under them and one Continue CTA at the bottom. That is
 * what this is: the code half is disabled until a code has been sent, and the CTA changes which
 * half it submits rather than the screen changing.
 *
 * *"Phone-OTP only · no Google Sign-In (US-11.5)"* is on the screen because it is a promise to the
 * driver, not a note to us: there is no other button, and there never will be one here.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun LoginScreen(
    onSignedIn: (OnboardingDestination) -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: LoginViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val destination by viewModel.destination.collectAsStateWithLifecycle()

    LaunchedEffect(destination) {
        destination?.let(onSignedIn)
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = {},
                navigationIcon = {
                    // Back from the code half returns to the number rather than leaving the
                    // screen: a mistyped number is the commonest reason to press it here.
                    val back = {
                        if (state.phase == LoginPhase.OTP) viewModel.editPhoneNumber() else onBack()
                    }
                    IconButton(onClick = back) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
            )
        },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.login_title),
                style = MaterialTheme.typography.headlineMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.login_phone_otp_only),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            PhoneNumberField(
                value = state.phone,
                onValueChange = viewModel::onPhoneChanged,
                enabled = state.phase == LoginPhase.PHONE && !state.busy,
                prefix = PhoneNumber.COUNTRY_CODE,
                placeholder = PhoneNumber.PLACEHOLDER,
                isError = state.error != null && state.phase == LoginPhase.PHONE,
            )

            OtpSection(state = state, viewModel = viewModel)

            state.error?.let { message ->
                Text(
                    text = stringResource(message),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            MageRideCta(
                label = stringResource(R.string.action_continue),
                enabled = state.canSubmit,
                loading = state.busy,
                onClick = viewModel::submit,
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )
        }
    }
}

/**
 * The wireframe's "enter code" divider, the six cells and the resend row.
 *
 * The resend is refused locally while the countdown runs. D-32 gives the number five OTPs an hour
 * and a 60-second bucket between them, so a tap inside the window would spend one of the five on
 * a message the server was never going to send.
 */
@Composable
private fun OtpSection(state: LoginState, viewModel: LoginViewModel, modifier: Modifier = Modifier) {
    val enabled = state.phase == LoginPhase.OTP && !state.busy

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            HorizontalDivider(modifier = Modifier.weight(1f))
            Text(
                text = stringResource(R.string.login_enter_code),
                modifier = Modifier.padding(horizontal = MageRideTheme.spacing.xs),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            HorizontalDivider(modifier = Modifier.weight(1f))
        }

        OtpEntry(
            value = state.otp,
            onValueChange = viewModel::onOtpChanged,
            length = LoginState.OTP_LENGTH,
            enabled = enabled,
            isError = state.error != null && state.phase == LoginPhase.OTP,
        )
        OtpProgress(value = state.otp, length = LoginState.OTP_LENGTH)

        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = stringResource(R.string.login_resend_code),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Box(modifier = Modifier.weight(1f))
            if (state.canResend) {
                TextButton(onClick = viewModel::resend, enabled = enabled) {
                    Text(text = stringResource(R.string.login_resend_action))
                }
            } else {
                Text(
                    text = stringResource(R.string.login_resend_in, state.resendInSeconds),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.outlineVariant,
                )
            }
        }

        state.attemptsRemaining?.let { attempts ->
            Text(
                text = stringResource(R.string.login_attempts_remaining, attempts),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
