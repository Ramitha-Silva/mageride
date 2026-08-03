package lk.mageride.driver.sharing

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.SolidBadge
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.component.VehicleChip
import lk.mageride.driver.ui.forType
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.ui.vehicleLabel
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.data.models.subscription.AccessRequest
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-028 · sharing management (Mode B), per vehicle** (US-4.1–4.4, US-4.7/4.8, AL-35).
 *
 * The wireframe, top to bottom: a `‹ Sharing (Mode B)` app bar, the **Vehicle** label and its
 * full-device-width chip row, the **Share with User ID** field, the **Expiry** row, the
 * `Grant access` CTA, *"Incoming requests · this vehicle"* and *"Current grantees · this vehicle"*.
 *
 * **AL-35's fence is drawn here and enforced in the view model.** The *"Showing sharing for …
 * assigned by …"* caption box is **removed** and the chip row takes the whole device width; the
 * lists below carry the wireframe's own *"· this vehicle"* in their headings, and
 * [SharingViewModel.selectVehicle] empties them before re-reading so a queue is never seen under
 * the wrong chip.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SharingScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: SharingViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.sharing_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
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
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            state.granted?.let {
                // US-4.3b — the grant is pending until the passenger accepts, so this is an offer
                // sent and not a subscriber gained. The grantee list below is deliberately not
                // touched until they answer.
                DashboardBanner(
                    text = stringResource(R.string.sharing_granted),
                    accent = MaterialTheme.colorScheme.secondary,
                )
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                SectionLabel(text = stringResource(R.string.sharing_vehicle_label))
                VehicleSelector(
                    vehicles = state.vehicles,
                    selectedVehicleId = state.selectedVehicleId,
                    onSelect = viewModel::selectVehicle,
                )

                if (state.hasNoShareableVehicle) {
                    Text(
                        text = stringResource(R.string.sharing_no_vehicles),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    return@Column
                }

                LabelledTextField(
                    label = stringResource(R.string.sharing_user_id_label),
                    value = state.userId,
                    onValueChange = viewModel::onUserIdChange,
                    isError = state.userIdRejected,
                    supporting = stringResource(
                        if (state.userIdRejected) R.string.sharing_user_id_invalid else R.string.sharing_user_id_help,
                    ),
                )
                ExpiryRow(state = state, onExpiryChange = viewModel::onExpiryChange)

                MageRideCta(
                    label = stringResource(R.string.sharing_grant_action),
                    onClick = viewModel::grant,
                    enabled = state.canGrant,
                    loading = state.granting,
                )

                RequestQueue(
                    state = state,
                    onAccept = { viewModel.decide(it, accept = true) },
                    onReject = { viewModel.decide(it, accept = false) },
                )
                GranteeList(grantees = state.grantees)
            }
        }
    }
}

/**
 * The wireframe's full-device-width chip row (AL-35).
 *
 * `weight(1f)` per chip is what "full device width" means when there are two of them, and it is the
 * shape the wireframe draws. The dot is MAP-03's per-type colour and the `FLEET` badge is US-13.9's
 * temporarily-assigned marker — `VehicleSummary.fleetName` is what registry sends for one.
 */
@Composable
private fun VehicleSelector(
    vehicles: List<VehicleSummary>,
    selectedVehicleId: String?,
    onSelect: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        vehicles.forEach { vehicle ->
            VehicleChip(
                label = vehicleLabel(vehicle),
                dot = MageRideTheme.vehicle.forType(vehicle.vehicleType),
                modifier = Modifier.weight(1f),
                selected = vehicle.vehicleId == selectedVehicleId,
                onClick = { onSelect(vehicle.vehicleId) },
                badge = vehicle.fleetName?.let {
                    {
                        SolidBadge(
                            label = stringResource(R.string.sharing_fleet_badge),
                            accent = MageRideTheme.mode.modeB,
                        )
                    }
                },
            )
        }
    }
}

/**
 * The wireframe's `Expiry   30 Jun 2026 ▾` row.
 *
 * Open-ended until a date is picked, because `CreateShareGrantRequest.expiresAt` is optional and
 * *"open-ended when omitted"*. What the picker answers is converted in [ShareExpiry] — read its
 * KDoc before changing anything here, because the two time-zone hops are where this goes wrong.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ExpiryRow(state: SharingState, onExpiryChange: (Timestamp?) -> Unit, modifier: Modifier = Modifier) {
    var picking by remember { mutableStateOf(false) }
    val locale = LocalConfiguration.current.locales[0]

    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = stringResource(R.string.sharing_expiry_label),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.weight(1f),
        )
        TextButton(onClick = { picking = true }) {
            Text(
                text = state.expiresAt
                    ?.let { ShareExpiry.label(it, locale) }
                    ?: stringResource(R.string.sharing_expiry_none),
            )
        }
        if (state.expiresAt != null) {
            TextButton(onClick = { onExpiryChange(null) }) {
                Text(text = stringResource(R.string.sharing_expiry_clear))
            }
        }
    }

    if (picking) {
        val pickerState = rememberDatePickerState()
        DatePickerDialog(
            onDismissRequest = { picking = false },
            confirmButton = {
                TextButton(
                    onClick = {
                        pickerState.selectedDateMillis?.let { onExpiryChange(ShareExpiry.endOfDay(it)) }
                        picking = false
                    },
                ) {
                    Text(text = stringResource(R.string.action_done))
                }
            },
            dismissButton = {
                TextButton(onClick = { picking = false }) {
                    Text(text = stringResource(R.string.action_cancel))
                }
            },
        ) {
            DatePicker(state = pickerState)
        }
    }
}

/** *"Incoming requests · this vehicle"* — US-4.4, scoped by the selector and never mixed. */
@Composable
private fun RequestQueue(
    state: SharingState,
    onAccept: (String) -> Unit,
    onReject: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.sharing_requests_heading))
        when {
            state.listsLoading -> Unit

            state.requests.isEmpty() -> Text(
                text = stringResource(R.string.sharing_requests_empty),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            else -> state.requests.forEach { request ->
                RequestRow(
                    request = request,
                    busy = state.busyRequestId == request.requestId,
                    onAccept = { onAccept(request.requestId) },
                    onReject = { onReject(request.requestId) },
                )
            }
        }
    }
}

/**
 * One incoming request — *"Sunethra · +94 77 712 0345 · PAX-77120"* with Accept and Reject.
 *
 * The number is `passengerMobileMasked` and arrives masked (AL-40/41/42); the wireframe prints a
 * full one, and a client cannot unmask what the directory never sent. The id under it is the
 * passenger's platform id — see [lk.mageride.driver.ui.PlatformId] on why it is not `PAX-77120`.
 */
@Composable
private fun RequestRow(
    request: AccessRequest,
    busy: Boolean,
    onAccept: () -> Unit,
    onReject: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = request.passengerName ?: stringResource(R.string.sharing_passenger_unnamed),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = listOfNotNull(request.passengerMobileMasked, request.passengerId)
                    .joinToString(" ${Symbols.DOT} "),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        TextButton(onClick = onAccept, enabled = !busy) {
            Text(text = stringResource(R.string.sharing_accept), color = MageRideTheme.status.success)
        }
        TextButton(onClick = onReject, enabled = !busy) {
            Text(text = stringResource(R.string.sharing_reject), color = MaterialTheme.colorScheme.error)
        }
    }
}

/** *"Current grantees · this vehicle"* — who can track it right now (US-4.7). */
@Composable
private fun GranteeList(grantees: List<Subscriber>, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.sharing_grantees_heading))
        if (grantees.isEmpty()) {
            Text(
                text = stringResource(R.string.sharing_grantees_empty),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            return@Column
        }
        grantees.forEach { grantee -> GranteeRow(grantee = grantee) }
    }
}

/** One grantee row, with the wireframe's green `Active` pill. */
@Composable
private fun GranteeRow(grantee: Subscriber, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Icon(
            imageVector = Icons.Outlined.Person,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = grantee.name ?: stringResource(R.string.sharing_passenger_unnamed),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = listOfNotNull(grantee.phoneMasked, grantee.userId)
                    .joinToString(" ${Symbols.DOT} "),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        StatusPill(label = stringResource(R.string.sharing_status_active), tone = StatusTone.DONE)
    }
}
