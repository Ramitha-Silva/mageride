package lk.mageride.driver.home

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardSheet
import lk.mageride.driver.ui.component.StatusCta
import lk.mageride.driver.ui.component.VehicleChip
import lk.mageride.driver.ui.forType
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * **SCR-DA-011 · the Mode A/B home dashboard — Start / End Journey.**
 *
 * *"This screen IS the driver's home dashboard whenever the active vehicle is a public-transport
 * bus (Mode A) or a private-transport vehicle (Mode B)"* — it replaces SCR-DA-010's standby map
 * rather than sitting beside it. It carries **only** Start Journey (green) and End Journey (red),
 * and the **vehicle type and number show below the route card**, which is the layout D2' spells out
 * and the one thing that distinguishes it from an ordinary tracking screen.
 *
 * **AL-32 — the dashboard can override the device.** A paired GPS tracker that saw ignition ON has
 * already opened the session, and the banner above says so; the driver may still End it here, and
 * may Start one by hand that the tracker's ignition-OFF will not close. Neither button is ever
 * disabled because of what the device did.
 *
 * **No fee** on Mode A (a bus journey is free); Mode B is a monthly subscription rather than a
 * per-trip charge, which is why neither draws SCR-DA-010's daily-fee chip.
 */
@Composable
internal fun JourneySheet(
    state: HomeState,
    onStart: () -> Unit,
    onEndOrRestart: () -> Unit,
    onChooseRoute: (String) -> Unit,
    onAutoEndChanged: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    var routeDialog by rememberSaveable { mutableStateOf(false) }

    DashboardSheet(modifier = modifier) {
        RouteCard(state = state, onChange = { routeDialog = true })

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.journey_vehicle),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
            state.vehicles.live?.let { vehicle ->
                VehicleChip(
                    label = "${stringResource(vehicle.vehicleType.labelRes())} · ${vehicle.registrationNumber}",
                    dot = MageRideTheme.vehicle.forType(vehicle.vehicleType),
                )
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.journey_auto_end_at_destination),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
            Switch(
                checked = state.autoEndAtDestination,
                onCheckedChange = onAutoEndChanged,
                enabled = !state.journey.isRunning,
            )
        }

        JourneyActions(state = state, onStart = onStart, onEndOrRestart = onEndOrRestart)
    }

    if (routeDialog) {
        RouteDialog(
            initial = state.journey.route?.routeShortName.orEmpty(),
            onConfirm = {
                routeDialog = false
                onChooseRoute(it)
            },
            onDismiss = { routeDialog = false },
        )
    }
}

/** The wireframe's route card — the route, the live duration and the distance (US-5.6). */
@Composable
private fun RouteCard(state: HomeState, onChange: () -> Unit, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = stringResource(R.string.journey_route),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        text = state.routeLabel(),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                }
                TextButton(onClick = onChange) {
                    Text(text = stringResource(R.string.journey_change))
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md)) {
                JourneyMetric(
                    label = stringResource(R.string.journey_duration),
                    value = MoneyFormat.clock(state.journeyElapsed.inWholeSeconds),
                    modifier = Modifier.weight(1f),
                )
                JourneyMetric(
                    label = stringResource(R.string.journey_distance),
                    value = MoneyFormat.distance(state.journeyDistanceM),
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

@Composable
private fun JourneyMetric(label: String, value: String, modifier: Modifier = Modifier) {
    Column(modifier = modifier) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(text = value, style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurface)
    }
}

/**
 * The two buttons, and **only** the two.
 *
 * A running journey offers End; a stopped one offers Start; an auto-ended one inside US-5.10's
 * five-minute grace offers **Restart** in End's place, because that is the only state a restart is
 * legal from — `restartableUntil` is present on an `AUTO_ENDED` session and on no other.
 */
@Composable
private fun JourneyActions(
    state: HomeState,
    onStart: () -> Unit,
    onEndOrRestart: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(modifier = modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
        StatusCta(
            label = stringResource(R.string.journey_start),
            accent = MageRideTheme.status.success,
            onClick = onStart,
            modifier = Modifier.weight(1f),
            enabled = !state.busy && !state.journey.isRunning && state.canGoOnline,
        )
        StatusCta(
            label = stringResource(
                if (state.journey.isRestartable) R.string.journey_restart else R.string.journey_end,
            ),
            accent = if (state.journey.isRestartable) {
                MaterialTheme.colorScheme.primary
            } else {
                MaterialTheme.colorScheme.error
            },
            onClick = onEndOrRestart,
            modifier = Modifier.weight(1f),
            enabled = !state.busy && (state.journey.isRunning || state.journey.isRestartable),
        )
    }
}

/**
 * The route chooser behind **Change ▾**.
 *
 * A typed route number rather than a picker, because there is no *"routes I drive"* read on
 * `transit.yaml` — `GET /v1/transit/routes/{routeId}` resolves one by id and nothing lists them for
 * a driver. What is typed is resolved against the active GTFS feed for its long name and remembered
 * on this handset, so it is typed once rather than every morning. Recorded as a spec gap in the
 * C070 handoff.
 */
@Composable
private fun RouteDialog(initial: String, onConfirm: (String) -> Unit, onDismiss: () -> Unit) {
    var value by remember { mutableStateOf(initial) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.journey_route_dialog_title)) },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                singleLine = true,
                label = { Text(text = stringResource(R.string.journey_route_hint)) },
            )
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(value.trim()) }, enabled = value.isNotBlank()) {
                Text(text = stringResource(R.string.action_continue))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.action_dismiss)) }
        },
    )
}

/** *"138 — Pettah ↔ Maharagama"*, or the prompt to pick one. */
@Composable
private fun HomeState.routeLabel(): String {
    val route = journey.route ?: return stringResource(R.string.journey_no_route)
    val long = route.routeLongName
    return if (long.isNullOrBlank()) route.routeShortName else "${route.routeShortName} — $long"
}
