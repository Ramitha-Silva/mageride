package lk.mageride.passenger.history

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-023 — one trip, its route and its receipt.
 *
 * The wireframe: the map with the trip's track, then Date / Vehicle / Driver / Distance / Total,
 * then `⬇ Receipt` and `⚑ Report issue`.
 *
 * **The distance shown is the one the fare was computed from** — `TripDetail.distanceKm`, the
 * Kalman-filtered figure (E-04) — and never a sum over the decoded polyline. When the geometry came
 * from the 1/min operational sample rather than full telemetry the contract says the distance is a
 * **lower bound**, and the screen says so rather than presenting a floor as a measurement.
 */
@Composable
internal fun TripDetailsScreen(onBack: () -> Unit, onReportIssue: () -> Unit, model: TripDetailsViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

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
                text = stringResource(R.string.trip_details_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Box(modifier = Modifier.weight(MAP_WEIGHT)) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                routePolyline = state.route,
                camera = state.route.firstOrNull()?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
            )
        }

        Column(
            modifier = Modifier
                .weight(SHEET_WEIGHT)
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            SectionLabel(text = stringResource(R.string.trip_details_receipt))

            state.trip?.let { trip ->
                DetailRow(stringResource(R.string.trip_details_driver), trip.driver?.name.orEmpty())
                DetailRow(
                    stringResource(R.string.trip_details_vehicle),
                    trip.driver?.registrationNumber.orEmpty(),
                )
                DetailRow(
                    stringResource(R.string.trip_details_distance),
                    trip.distanceKm?.let { stringResource(R.string.summary_km, it) } ?: MoneyFormat.EMPTY,
                )
                DetailRow(
                    stringResource(R.string.summary_total),
                    trip.fareMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
                )
            }

            // The contract's own caveat, surfaced. A lower bound presented as a measurement is how
            // a passenger ends up arguing with a receipt that was never claiming to be exact.
            if (state.approximate) {
                Text(
                    text = stringResource(R.string.trip_details_approximate),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            OutlinedButton(onClick = onReportIssue, modifier = Modifier.fillMaxWidth()) {
                Icon(imageVector = Icons.Filled.Flag, contentDescription = null)
                Text(
                    text = stringResource(R.string.trip_details_report),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }
        }
    }
}

@Composable
private fun DetailRow(label: String, value: String) {
    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

private const val MAP_WEIGHT = 0.45f
private const val SHEET_WEIGHT = 0.55f
