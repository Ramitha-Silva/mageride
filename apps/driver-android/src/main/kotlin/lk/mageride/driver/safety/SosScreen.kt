package lk.mageride.driver.safety

import androidx.activity.compose.BackHandler
import androidx.annotation.StringRes
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.ui.theme.SosColors
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.safety.SosSmsStatus
import org.koin.androidx.compose.koinViewModel
import org.koin.core.parameter.parametersOf

/**
 * **SCR-DA-032 · the driver SOS** (US-12.8, AL-13, D-33).
 *
 * The wireframe on `#2A0A0A`: *"Driver SOS"*, a 128 dp `error` disc inside a translucent halo of
 * itself, the line *"Sending GPS + active trip to emergency contact via SMS…"*, and the contact
 * card carrying *"Amma · +94 77 000 1111"* with a `Sent` pill.
 *
 * **Back cancels while the alarm is armed, and is swallowed while it is in flight.** Popping the
 * screen clears the view model, which cancels the countdown — so a driver who opened this by mistake
 * gets the same outcome from Back as from **Cancel**, which is the safe direction. Once the request
 * has left, there is nothing to go back *to*: `POST /v1/sos` is not revocable, and a gesture that
 * looked like it un-sent an alarm would be the worst affordance on this surface.
 *
 * @param onFinished The driver cancelled before the alarm went, or closed the dispatched state.
 */
@Composable
internal fun SosScreen(rideId: Ulid, onFinished: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: SosViewModel = koinViewModel { parametersOf(rideId) }
    val state by viewModel.state.collectAsStateWithLifecycle()

    BackHandler(enabled = state.stage == SosStage.SENDING) { /* an alarm in flight is not dismissible */ }

    Surface(modifier = modifier.fillMaxSize(), color = SosColors.background) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(MageRideTheme.spacing.md),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md, Alignment.CenterVertically),
        ) {
            Text(
                text = stringResource(R.string.sos_title),
                style = MaterialTheme.typography.titleLarge,
                color = SosColors.onSos,
            )

            SosDisc(state = state, onRaise = viewModel::raise)

            Text(
                text = stringResource(statusLine(state)),
                style = MaterialTheme.typography.labelLarge,
                color = if (state.stage == SosStage.FAILED) MaterialTheme.colorScheme.error else SosColors.hint,
                textAlign = TextAlign.Center,
            )

            state.error?.let { message ->
                Text(
                    text = stringResource(message),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                    textAlign = TextAlign.Center,
                )
            }

            ContactCard(state = state)

            Spacer(modifier = Modifier.size(MageRideTheme.spacing.xs))

            SosFooter(state = state, viewModel = viewModel, onFinished = onFinished)
        }
    }
}

/**
 * The wireframe's disc, and the only control that raises the alarm.
 *
 * While armed it carries the countdown rather than the word: the number is what tells a driver who
 * pressed it by accident that they still have a moment, and D-33's budget is why that moment is
 * three seconds and not ten (see [SosViewModel.COUNTDOWN_SECONDS]).
 */
@Composable
private fun SosDisc(state: SosState, onRaise: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(ControlTokens.SosButton + ControlTokens.SosHalo + ControlTokens.SosHalo)
            .background(SosColors.halo, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Surface(
            modifier = Modifier.size(ControlTokens.SosButton),
            shape = CircleShape,
            color = MaterialTheme.colorScheme.error,
            enabled = state.stage == SosStage.ARMED,
            onClick = onRaise,
        ) {
            Box(contentAlignment = Alignment.Center) {
                Text(
                    text = when (state.stage) {
                        SosStage.ARMED -> if (state.awaitingPosition) {
                            SosLabels.SOS
                        } else {
                            state.secondsLeft.toString()
                        }

                        else -> SosLabels.SOS
                    },
                    style = MaterialTheme.typography.headlineMedium,
                    color = MageRideTheme.status.onStatus,
                )
            }
        }
    }
}

/**
 * *"Amma · +94 77 000 1111"* with its `Sent` pill (AL-13).
 *
 * A driver with **no** contact on file is told so here rather than being refused the alarm: the
 * event is still recorded and still reaches the admin live feed, and safety-svc answers
 * `SosSmsStatus.NO_CONTACT` for the leg that has nowhere to go. The fix is SCR-DA-029, which is
 * where the contact is set.
 */
@Composable
private fun ContactCard(state: SosState, modifier: Modifier = Modifier) {
    val contact = state.contact

    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        color = SosColors.surface,
        border = BorderStroke(ControlTokens.Border, SosColors.outline),
    ) {
        Row(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = contact?.let { contactLabel(it) } ?: stringResource(R.string.sos_no_contact),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyMedium,
                color = SosColors.onSos,
            )
            state.smsStatus?.let { StatusPill(label = stringResource(smsLabel(it)), tone = smsTone(it)) }
        }
    }
}

/** Cancel while armed; Close once the alarm has gone; Try again when it never left the handset. */
@Composable
private fun SosFooter(
    state: SosState,
    viewModel: SosViewModel,
    onFinished: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(modifier = modifier) {
        when (state.stage) {
            SosStage.ARMED -> TextButton(
                onClick = {
                    viewModel.cancelCountdown()
                    onFinished()
                },
            ) {
                Text(text = stringResource(R.string.action_cancel), color = SosColors.onSos)
            }

            SosStage.SENDING -> Text(
                text = stringResource(R.string.sos_sending),
                style = MaterialTheme.typography.labelLarge,
                color = SosColors.hint,
            )

            SosStage.DISPATCHED -> TextButton(onClick = onFinished) {
                Text(text = stringResource(R.string.action_close), color = SosColors.onSos)
            }

            SosStage.FAILED -> Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                TextButton(onClick = viewModel::retry) {
                    Text(text = stringResource(R.string.action_retry), color = SosColors.onSos)
                }
                TextButton(onClick = onFinished) {
                    Text(text = stringResource(R.string.action_close), color = SosColors.hint)
                }
            }
        }
    }
}

/**
 * The word on the disc, and why it is not copy.
 *
 * `SOS` is an international distress signal, not a sentence: it is the same three letters in
 * Sinhala, Tamil and English, and three identical values in the three `strings.xml` files is exactly
 * what `StringResourceTest` (correctly) reads as a key nobody translated. Same rule as `Rs`, `L3`
 * and `+94` (C068). The *title* above it is ordinary copy and is translated.
 */
internal object SosLabels {
    const val SOS: String = "SOS"
}

/** *"Amma · +94 77 000 1111"*. The number is a proper noun and the dot is [Symbols.DOT]. */
private fun contactLabel(contact: EmergencyContact): String = "${contact.name} ${Symbols.DOT} ${contact.phone}"

/** The line under the disc. Four states, four sentences. */
@StringRes
private fun statusLine(state: SosState): Int = when (state.stage) {
    SosStage.ARMED -> if (state.awaitingPosition) R.string.sos_waiting_position else R.string.sos_armed
    SosStage.SENDING -> R.string.sos_sending_body
    SosStage.DISPATCHED -> R.string.sos_dispatched
    SosStage.FAILED -> R.string.sos_failed
}

/**
 * What the pill says about D-33's parallel gateways.
 *
 * `Failed` is **not** an error state on this screen and does not colour like one: the alert is
 * recorded and is on the admin live feed either way, and telling somebody in trouble that nothing
 * happened would be worse than telling them the SMS leg did not manage it.
 */
@StringRes
private fun smsLabel(status: SosSmsStatus): Int = when (status) {
    SosSmsStatus.DISPATCHED -> R.string.sos_sms_sent
    SosSmsStatus.FAILED -> R.string.sos_sms_failed
    SosSmsStatus.NO_CONTACT -> R.string.sos_sms_no_contact
}

private fun smsTone(status: SosSmsStatus): StatusTone = when (status) {
    SosSmsStatus.DISPATCHED -> StatusTone.DONE
    SosSmsStatus.FAILED, SosSmsStatus.NO_CONTACT -> StatusTone.PENDING
}
