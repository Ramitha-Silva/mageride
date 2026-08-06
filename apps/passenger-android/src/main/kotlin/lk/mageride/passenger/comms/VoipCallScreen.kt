package lk.mageride.passenger.comms

import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.annotation.StringRes
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.CallEnd
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MicOff
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.core.net.toUri
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.DateFormat
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.CallColors
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * **SCR-PA-028 · the in-app VoIP call** (US-6A.16, D-24, AL-48).
 *
 * The wireframe, top to bottom on `#15171B`: *"In-app call · number hidden"*, a large avatar disc,
 * the driver's name, the state line (*"Connected · 01:24"*), then the 🔇 / 🔊 pair and a red
 * hang-up disc.
 *
 * **Dark in both appearances** — see [CallColors]. A call screen that turned white in the light
 * theme would be a different screen twice a day, and a phone call has one appearance.
 *
 * **Back hangs up rather than navigating.** Leaving a call by swiping would leave the room joined
 * behind a screen the passenger can no longer see; [VoipCallViewModel.hangUp] reports the outcome
 * and then [onFinished] pops.
 *
 * @param onFinished The call is over — hung up, dialled normally, or dismissed.
 */
@Composable
internal fun VoipCallScreen(onFinished: () -> Unit, model: VoipCallViewModel, modifier: Modifier = Modifier) {
    val state by model.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    BackHandler(enabled = !state.finished) { model.hangUp() }

    Surface(modifier = modifier.fillMaxSize(), color = CallColors.background) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(MageRideTheme.spacing.md),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
        ) {
            Spacer(modifier = Modifier.size(MageRideTheme.spacing.xxl))

            // "number hidden" is a PROPERTY of a VoIP call, not a promise the platform makes any
            // more: AL-48 withdrew masking, and the caption is true only because a LiveKit room
            // carries no MSISDN. It is deliberately absent from the failure state, where the very
            // next thing offered is a dial of the driver's real number.
            if (state.stage != CallStage.FAILED) {
                Text(
                    text = stringResource(R.string.call_in_app),
                    style = MaterialTheme.typography.labelMedium,
                    color = CallColors.hint,
                )
            }

            CalleeAvatar()

            Text(
                text = state.calleeName ?: stringResource(R.string.call_driver),
                style = MaterialTheme.typography.headlineSmall,
                color = CallColors.onCall,
                textAlign = TextAlign.Center,
            )

            CallStatusLine(state = state)

            Spacer(modifier = Modifier.weight(1f))

            if (state.canDialDirectly) {
                DirectDialPrompt(onDial = model::dialDirectly)
            }

            CallActions(state = state, model = model)

            Spacer(modifier = Modifier.size(MageRideTheme.spacing.lg))
        }
    }

    // AL-48's fallback dial. `ACTION_DIAL` rather than `ACTION_CALL`: it opens the dialler with the
    // number already in it and needs no `CALL_PHONE` permission — the same choice `CallChooserSheet`
    // makes for SCR-PA-015a's "Normal call", and the transparency US-26.5 asks for made concrete.
    LaunchedEffect(state.dialNumber) {
        state.dialNumber?.let { number ->
            runCatching {
                context.startActivity(
                    Intent(Intent.ACTION_DIAL, "tel:$number".toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                )
            }
            model.consumeDial()
        }
    }

    LaunchedEffect(state.finished) {
        if (state.finished) onFinished()
    }
}

/** The wireframe's `.avatar lg` — a plain disc, because no photo of a driver reaches this app. */
@Composable
private fun CalleeAvatar(modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(ControlTokens.AvatarLarge)
            .background(CallColors.surface, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = Icons.Filled.Person,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.CallEnd),
            tint = CallColors.hint,
        )
    }
}

/**
 * The one line that says where the call is — *"Connecting…"*, *"Connected · 01:24"* or the failure.
 *
 * The failure copy is chosen by [VoipFailure] rather than being one sentence, because the three
 * causes need different advice: a ride that has ended has nobody to dial, and a build with no media
 * client is not a network problem the passenger can wait out.
 */
@Composable
private fun CallStatusLine(state: VoipCallState, modifier: Modifier = Modifier) {
    when (state.stage) {
        CallStage.CONNECTING -> Text(
            text = stringResource(R.string.call_connecting),
            modifier = modifier,
            style = MaterialTheme.typography.labelLarge,
            color = CallColors.connected,
        )

        CallStage.CONNECTED -> Text(
            text = stringResource(R.string.call_connected, DateFormat.timer(state.seconds)),
            modifier = modifier,
            style = MaterialTheme.typography.labelLarge,
            color = CallColors.connected,
        )

        CallStage.FAILED -> Text(
            text = stringResource(failureMessage(state.failure)),
            modifier = modifier,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.error,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * **AL-48's *"Call normally instead?"*** — the whole of what a VoIP failure falls back to.
 *
 * A direct cellular dial of the driver's real number. The masked-number bridge and D-25's masked-SMS
 * relay were both withdrawn, so there is no third option to offer and none is drawn — and no copy on
 * this screen claims the passenger's number stays hidden once they take it.
 */
@Composable
private fun DirectDialPrompt(onDial: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.call_fallback_question),
            style = MaterialTheme.typography.bodyMedium,
            color = CallColors.hint,
            textAlign = TextAlign.Center,
        )
        MageRideCta(label = stringResource(R.string.call_fallback_action), onClick = onDial)
    }
}

/** The wireframe's two `.fab`s and its red disc. Mute and speaker are inert once a call has failed. */
@Composable
private fun CallActions(state: VoipCallState, model: VoipCallViewModel, modifier: Modifier = Modifier) {
    val live = state.stage != CallStage.FAILED

    Column(
        modifier = modifier,
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
    ) {
        if (live) {
            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md)) {
                CallToggle(
                    icon = if (state.muted) Icons.Filled.MicOff else Icons.Filled.Mic,
                    label = stringResource(if (state.muted) R.string.call_unmute else R.string.call_mute),
                    active = state.muted,
                    onClick = model::toggleMute,
                )
                CallToggle(
                    icon = if (state.speakerOn) {
                        Icons.AutoMirrored.Filled.VolumeUp
                    } else {
                        Icons.AutoMirrored.Filled.VolumeOff
                    },
                    label = stringResource(R.string.call_speaker),
                    active = state.speakerOn,
                    onClick = model::toggleSpeaker,
                )
            }
        }

        Surface(
            modifier = Modifier.size(ControlTokens.CallEnd),
            shape = CircleShape,
            color = MaterialTheme.colorScheme.error,
            onClick = model::hangUp,
        ) {
            Icon(
                imageVector = Icons.Filled.CallEnd,
                contentDescription = stringResource(if (live) R.string.call_hang_up else R.string.action_close),
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                tint = MageRideTheme.status.onStatus,
            )
        }
    }
}

/** One of the wireframe's `.fab` controls — a 44 dp disc that reads as pressed when it is on. */
@Composable
private fun CallToggle(
    icon: ImageVector,
    label: String,
    active: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier.size(ControlTokens.CallAction),
        shape = CircleShape,
        color = if (active) CallColors.onCall else CallColors.surface,
        onClick = onClick,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = label,
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            tint = if (active) CallColors.background else CallColors.onCall,
        )
    }
}

/** The copy for a failure, by cause. */
@StringRes
private fun failureMessage(failure: VoipFailure?): Int = when (failure) {
    VoipFailure.SIGNALLING -> R.string.call_failed_signalling

    VoipFailure.MEDIA -> R.string.call_failed_media

    // Not a network condition — this build carries no WebRTC client at all. See `AbsentVoipEngine`.
    VoipFailure.NO_MEDIA_CLIENT, null -> R.string.call_failed_unavailable
}
