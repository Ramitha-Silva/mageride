package lk.mageride.passenger.ride

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-014 — *"Finding a driver…"*.
 *
 * The wireframe: a radar pulse, *"Sedan · usually under 2 min · `1:34` left"*, and **Cancel
 * (free)**. Two minutes with nothing assigned is US-6A.11's *"No drivers available"* plus a retry.
 *
 * **Cancel is free here and says so on the button.** US-6A.9 — nothing has been accepted, so
 * nothing is owed. The moment a driver accepts, this screen is replaced by SCR-PA-015, where the
 * same action costs Rs 50 and carries a confirm dialog. The two are different screens precisely so
 * the two cancels can never be confused.
 *
 * @param onAssigned A driver accepted — SCR-PA-015 replaces this screen.
 */
@Composable
internal fun FindingDriverScreen(
    onAssigned: () -> Unit,
    onCancelled: () -> Unit,
    onRetry: () -> Unit,
    model: ActiveRideViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.assigned) { if (state.assigned) onAssigned() }
    LaunchedEffect(state.cancelled) { if (state.cancelled) onCancelled() }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.lg),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        RadarPulse()

        Text(
            text = if (state.noDriver) {
                stringResource(R.string.finding_none)
            } else {
                stringResource(R.string.finding_title)
            },
            modifier = Modifier.padding(top = MageRideTheme.spacing.lg),
            style = MaterialTheme.typography.titleLarge,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )

        Text(
            text = if (state.noDriver) {
                stringResource(R.string.finding_none_caption)
            } else {
                stringResource(R.string.finding_caption, state.countdown)
            },
            modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )

        state.error?.let {
            InlineError(
                message = stringResource(it),
                modifier = Modifier.padding(top = MageRideTheme.spacing.sm),
            )
        }

        if (state.noDriver) {
            MageRideCta(
                label = stringResource(R.string.finding_retry),
                onClick = onRetry,
                modifier = Modifier.padding(top = MageRideTheme.spacing.lg),
            )
        }

        OutlinedButton(
            onClick = model::confirmCancel,
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = MageRideTheme.spacing.sm),
            enabled = !state.cancelling,
        ) {
            // "Cancel (free)" — the word is on the control, not in a dialog, because before
            // acceptance there is nothing to confirm and nothing to warn about (US-6A.9).
            Text(stringResource(R.string.finding_cancel_free))
        }
    }
}

/** The wireframe's radar sweep — a widening ring, drawn rather than shipped as an asset. */
@Composable
private fun RadarPulse() {
    val transition = rememberInfiniteTransition(label = "radar")
    val progress by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = PULSE_MILLIS, easing = LinearEasing),
            repeatMode = RepeatMode.Restart,
        ),
        label = "sweep",
    )
    val colour = MaterialTheme.colorScheme.primary

    Box(contentAlignment = Alignment.Center) {
        Canvas(modifier = Modifier.size(ControlTokens.RadarSize)) {
            val maxRadius = size.minDimension / 2
            drawCircle(color = colour, radius = maxRadius * progress, alpha = 1f - progress)
            drawCircle(color = colour, radius = maxRadius * CORE_FRACTION)
        }
    }
}

private const val PULSE_MILLIS = 1_600
private const val CORE_FRACTION = 0.12f
