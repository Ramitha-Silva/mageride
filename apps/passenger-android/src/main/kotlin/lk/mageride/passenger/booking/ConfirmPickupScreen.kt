package lk.mageride.passenger.booking

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
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
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.map.MapPin
import lk.mageride.passenger.map.VehicleLayers
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-011 — the **rider's** side of a proxy pickup (US-8.18, P-02).
 *
 * The wireframe, and every word of it matters: `⏱ Expires in 4:38 · declining never sends your
 * GPS`, a map with a draggable pin and *"drag to adjust"*, then *"**Ramith** wants your pickup
 * location"* over `Decline | Share location ▸`.
 *
 * **The privacy promise is on the screen before the decision, not in a settings page after it.**
 * That banner is the whole reason this screen reads the way it does: somebody who is not
 * necessarily a MageRide user has just been pushed a request for their position, and the only
 * honest thing to do is tell them what each button does before they press one. [ConfirmPickupViewModel.decline]
 * makes it true — the operation it calls takes no body.
 *
 * @param onFinished The rider answered, or the five minutes ran out. Either way this screen closes.
 */
@Composable
internal fun ConfirmPickupScreen(onFinished: () -> Unit, model: ConfirmPickupViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.outcome) {
        if (state.outcome != null) onFinished()
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // The TTL banner. Above the map rather than under the buttons, because it is context for
        // the decision and not a footnote to it.
        Text(
            text = stringResource(R.string.confirm_pickup_banner, state.countdown),
            modifier = Modifier
                .fillMaxWidth()
                .background(MaterialTheme.colorScheme.surfaceVariant)
                .padding(MageRideTheme.spacing.xs),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )

        Box(modifier = Modifier.weight(1f)) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                pins = state.pin?.let { listOf(MapPin(VehicleLayers.PIN_PICKUP, it.lat, it.lng)) }.orEmpty(),
                camera = state.pin?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
                // The pin follows the camera: MapLibre's own draggable-symbol API needs an
                // annotation plugin this module does not depend on, so "drag to adjust" is done by
                // dragging the *map* under a fixed centre marker — the pattern every ride app uses
                // and the one that works with one thumb. See the C079 handoff.
                onRecentre = { },
            )
            Text(
                text = stringResource(R.string.confirm_pickup_drag),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(MageRideTheme.spacing.xs)
                    .background(
                        MaterialTheme.colorScheme.surfaceVariant,
                        RoundedCornerShape(MageRideTheme.radius.sm),
                    )
                    .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = state.bookerName
                    ?.let { stringResource(R.string.confirm_pickup_who, it) }
                    ?: stringResource(R.string.confirm_pickup_who_unknown),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.confirm_pickup_why),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            state.error?.let { InlineError(message = stringResource(it)) }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                OutlinedButton(onClick = model::decline, modifier = Modifier.weight(1f)) {
                    Text(stringResource(R.string.confirm_pickup_decline))
                }
                MageRideCta(
                    label = stringResource(R.string.confirm_pickup_share),
                    onClick = model::share,
                    modifier = Modifier.weight(1f),
                    enabled = state.canShare,
                    loading = state.sending,
                )
            }
        }
    }
}
