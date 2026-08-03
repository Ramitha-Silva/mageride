package lk.mageride.driver.home

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardSheet
import lk.mageride.driver.ui.component.OnlineToggle
import lk.mageride.driver.ui.component.VehicleChip
import lk.mageride.driver.ui.forType
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * **SCR-DA-010 · the Mode C standby sheet.**
 *
 * The wireframe, top to bottom: the `◉ ONLINE — Mode C` bar with its switch, the *"Live vehicle"*
 * row carrying the reg no selected in SCR-DA-026 (US-9.6), and the row that pairs the
 * `⮕ Directional ▸` chip with today's trips and earnings.
 *
 * **No hamburger** (AL-31). Nothing on this sheet opens a drawer; navigation is the bottom-nav
 * **Menu** tab, which the shell draws.
 *
 * @param onOpenVehicles US-9.6's empty state — *"Add or get assigned a vehicle to go online"*,
 *   which routes to My Vehicles and raises SCR-DA-026a when the list is empty.
 */
@Composable
internal fun StandbySheet(
    state: HomeState,
    onToggleOnline: (Boolean) -> Unit,
    onOpenDirectional: () -> Unit,
    onOpenVehicles: () -> Unit,
    modifier: Modifier = Modifier,
) {
    DashboardSheet(modifier = modifier) {
        OnlineToggle(
            label = stringResource(if (state.online) R.string.home_online else R.string.home_offline),
            online = state.online,
            enabled = state.canGoOnline,
            busy = state.busy,
            onToggle = onToggleOnline,
        )

        if (state.needsVehicle) {
            NoVehicleNotice(onOpenVehicles = onOpenVehicles)
        } else {
            LiveVehicleRow(state = state)
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            DirectionalChip(state = state, onClick = onOpenDirectional)
            Text(
                text = todayStats(state),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
        }
    }
}

/**
 * *"Live vehicle · Three-wheeler · ABC-1234"* (US-9.6, SCR-DA-026).
 *
 * The reg no is the single active publisher's — the vehicle whose id is the MQTT username, so what
 * this row names is literally what the broker will see at CONNECT.
 */
@Composable
private fun LiveVehicleRow(state: HomeState, modifier: Modifier = Modifier) {
    val vehicle = state.vehicles.live ?: return
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = stringResource(R.string.home_live_vehicle),
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f),
        )
        VehicleChip(
            label = "${stringResource(vehicle.vehicleType.labelRes())} · ${vehicle.registrationNumber}",
            dot = MageRideTheme.vehicle.forType(vehicle.vehicleType),
        )
    }
}

/** US-9.6's gate, said out loud. The tap goes to My Vehicles, whose empty state is SCR-DA-026a. */
@Composable
private fun NoVehicleNotice(onOpenVehicles: () -> Unit, modifier: Modifier = Modifier) {
    Column(modifier = modifier.fillMaxWidth()) {
        Text(
            text = stringResource(R.string.home_needs_vehicle),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        TextButton(onClick = onOpenVehicles) {
            Text(text = stringResource(R.string.home_add_vehicle), style = MaterialTheme.typography.labelLarge)
        }
    }
}

/**
 * The `⮕ Directional ▸` chip — SCR-DA-012's *"Directional entry chip showing active filter
 * status"*, merged into this sheet with the rest of that screen.
 */
@Composable
private fun DirectionalChip(state: HomeState, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val filter = state.standing.directional
    val active = filter?.active == true

    AssistChip(
        onClick = onClick,
        modifier = modifier,
        label = {
            Text(
                text = if (active && filter?.label != null) {
                    filter.label.orEmpty()
                } else {
                    stringResource(R.string.home_directional)
                },
                style = MaterialTheme.typography.labelLarge,
            )
        },
        leadingIcon = {
            Icon(
                imageVector = Icons.AutoMirrored.Outlined.ArrowForward,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.ChipIcon),
            )
        },
        colors = if (active) {
            AssistChipDefaults.assistChipColors(
                containerColor = MaterialTheme.colorScheme.primaryContainer,
                labelColor = MaterialTheme.colorScheme.onPrimaryContainer,
                leadingIconContentColor = MaterialTheme.colorScheme.onPrimaryContainer,
            )
        } else {
            AssistChipDefaults.assistChipColors()
        },
    )
}

/** *"Today: 4 trips · Rs 3,180"*. Blank while the earnings read is still in flight (shimmer). */
@Composable
private fun todayStats(state: HomeState): String {
    val earnings = state.standing.earnings ?: return stringResource(R.string.home_today_pending)
    return stringResource(
        R.string.home_today_stats,
        earnings.trips,
        MoneyFormat.rupees(earnings.netMinor),
    )
}
