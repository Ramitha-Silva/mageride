package lk.mageride.passenger.safety

import android.content.Intent
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.passenger.ui.theme.SosColors
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.safety.SosSmsStatus

/**
 * **SCR-PA-029 · the passenger SOS** (US-12.1, AL-13, D-33, D-34).
 *
 * The wireframe on `#2A0A0A`: *"Emergency SOS"*, a 130 px `error` disc inside a translucent halo of
 * itself, the line *"Sending GPS + trip to emergency contacts via SMS…"*, and the contact card
 * carrying *"Amma · +94 77 000 1111"* with a `Sent` pill.
 *
 * **Back cancels while the alarm is armed, and is swallowed while it is in flight.** Popping the
 * screen clears the view model, which cancels the countdown — so a passenger who opened this by
 * mistake gets the same outcome from Back as from **Cancel**, which is the safe direction. Once the
 * request has left there is nothing to go back *to*: `POST /v1/sos` is not revocable, and a gesture
 * that looked like it un-sent an alarm would be the worst affordance on this surface.
 *
 * **The live trip link is drawn only once the alarm has gone.** D-34's token is minted after
 * dispatch — see [SosViewModel] — so the link cannot appear before the thing it is about.
 *
 * @param onFinished The passenger cancelled before the alarm went, or closed the dispatched state.
 */
@Composable
internal fun SosScreen(onFinished: () -> Unit, model: SosViewModel, modifier: Modifier = Modifier) {
    val state by model.state.collectAsStateWithLifecycle()

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

            SosDisc(state = state, onRaise = model::raise)

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

            ContactList(state = state)

            state.shareLink?.let { link -> ShareLinkCard(link = link) }

            Spacer(modifier = Modifier.size(MageRideTheme.spacing.xs))

            SosFooter(state = state, model = model, onFinished = onFinished)
        }
    }
}

/**
 * The wireframe's disc, and the only control that raises the alarm.
 *
 * While armed it carries the countdown rather than the word: the number is what tells a passenger
 * who pressed it by accident that they still have a moment, and D-33's budget is why that moment is
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
                    text = if (state.stage == SosStage.ARMED && !state.awaitingPosition) {
                        state.secondsLeft.toString()
                    } else {
                        SosLabels.SOS
                    },
                    style = MaterialTheme.typography.headlineMedium,
                    color = MageRideTheme.status.onStatus,
                )
            }
        }
    }
}

/**
 * *"Amma · +94 77 000 1111"* with its `Sent` pill (AL-13, US-12.1).
 *
 * D2' §SCR-PA-029 draws the contacts as a list and this app keeps one (SCR-PA-027b's `＋ Add SOS
 * contact`), so every row is drawn — but **only the primary wears the status pill**, because D-33's
 * five-second path reads one denormalised number and that is the one the SMS goes to. Showing `Sent`
 * against three names when one was texted would be the screen inventing a fan-out the platform does
 * not do.
 *
 * A passenger with **no** contact on file is told so here, with the fix named: SCR-PA-027b is where
 * the list is edited, and an empty list is what makes `POST /v1/sos` answer
 * `400 no-emergency-contact`.
 */
@Composable
private fun ContactList(state: SosState, modifier: Modifier = Modifier) {
    val primary = state.primaryContact

    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        color = SosColors.surface,
        border = BorderStroke(ControlTokens.Border, SosColors.outline),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            if (state.contacts.isEmpty()) {
                Text(
                    text = stringResource(
                        if (state.contactsLoaded) R.string.sos_no_contact else R.string.sos_contacts_loading,
                    ),
                    style = MaterialTheme.typography.bodyMedium,
                    color = SosColors.onSos,
                )
                if (state.warnsNoContact) {
                    Text(
                        text = stringResource(R.string.sos_no_contact_hint),
                        style = MaterialTheme.typography.labelSmall,
                        color = SosColors.hint,
                    )
                }
                return@Column
            }

            state.contacts.forEach { contact ->
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                ) {
                    Text(
                        text = contactLabel(contact),
                        modifier = Modifier.weight(1f),
                        style = MaterialTheme.typography.bodyMedium,
                        color = SosColors.onSos,
                    )
                    if (contact == primary) {
                        state.smsStatus?.let { SmsPill(status = it) }
                    }
                }
            }
        }
    }
}

/**
 * D-34's live trip link, and the share sheet that sends it (US-12.1).
 *
 * The token is the credential and the public view is **live only — there is no replay**, so a link
 * that leaks stops being useful the moment the trip ends. That is what makes handing one to a
 * stranger over WhatsApp a reasonable thing to offer somebody in trouble.
 */
@Composable
private fun ShareLinkCard(link: String, modifier: Modifier = Modifier) {
    val context = LocalContext.current

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.sos_share_hint),
            style = MaterialTheme.typography.labelSmall,
            color = SosColors.hint,
            textAlign = TextAlign.Center,
        )
        TextButton(
            onClick = {
                val send = Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_TEXT, link)
                }
                runCatching { context.startActivity(Intent.createChooser(send, null)) }
            },
        ) {
            Text(text = stringResource(R.string.sos_share_action), color = SosColors.onSos)
        }
    }
}

/** Cancel while armed; Close once the alarm has gone; Try again when it never left the handset. */
@Composable
private fun SosFooter(state: SosState, model: SosViewModel, onFinished: () -> Unit, modifier: Modifier = Modifier) {
    Box(modifier = modifier) {
        when (state.stage) {
            SosStage.ARMED -> TextButton(
                onClick = {
                    model.cancelCountdown()
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
                TextButton(onClick = model::retry) {
                    Text(text = stringResource(R.string.action_retry), color = SosColors.onSos)
                }
                TextButton(onClick = onFinished) {
                    Text(text = stringResource(R.string.action_close), color = SosColors.hint)
                }
            }
        }
    }
}

/** The wireframe's `pill-status` on the contact row. */
@Composable
private fun SmsPill(status: SosSmsStatus, modifier: Modifier = Modifier) {
    val (label, tint) = when (status) {
        SosSmsStatus.DISPATCHED -> R.string.sos_sms_sent to MageRideTheme.status.success

        // NOT an error tint. The alert is recorded and is on the admin live feed either way, and
        // colouring the SMS leg red would tell somebody in trouble that nothing happened.
        SosSmsStatus.FAILED -> R.string.sos_sms_failed to MageRideTheme.status.warning

        SosSmsStatus.NO_CONTACT -> R.string.sos_sms_no_contact to MageRideTheme.status.warning
    }

    Text(
        text = stringResource(label),
        modifier = modifier
            .background(tint.copy(alpha = PILL_TINT), RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = SosColors.onSos,
    )
}

/**
 * The word on the disc, and why it is not copy.
 *
 * `SOS` is an international distress signal, not a sentence: it is the same three letters in
 * Sinhala, Tamil and English, and three identical values in the three `strings.xml` files is exactly
 * what `StringResourceTest` (correctly) reads as a key nobody translated. Same rule as `Rs` and
 * `+94`. The **title** above it is ordinary copy and is translated, which is why `ride_sos` on
 * SCR-PA-015 reads *"හදිසි උදව්"* and this does not.
 */
internal object SosLabels {
    const val SOS: String = "SOS"
}

/** *"Amma · +94 77 000 1111"*. Both halves are proper nouns, and the dot is the wireframe's. */
private fun contactLabel(contact: EmergencyContact): String = "${contact.name} $SEPARATOR ${contact.phone}"

/** The line under the disc. Four states, four sentences. */
@StringRes
private fun statusLine(state: SosState): Int = when (state.stage) {
    SosStage.ARMED -> if (state.awaitingPosition) R.string.sos_waiting_position else R.string.sos_armed
    SosStage.SENDING -> R.string.sos_sending_body
    SosStage.DISPATCHED -> R.string.sos_dispatched
    SosStage.FAILED -> R.string.sos_failed
}

private const val SEPARATOR = "·"

/** The tint strength every status pill in this app is drawn at (C082's `StatusPill`). */
private const val PILL_TINT = 0.16f
