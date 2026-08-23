package lk.mageride.driver.vehicle

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.forType
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.util.BusinessCalendar
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-026 · vehicle management** and **SCR-DA-026a · the no-vehicles popup**.
 *
 * The wireframe top to bottom: a ‹ / *"My vehicles"* / ＋ bar, the blue banner *"Only one vehicle
 * can be live at a time"*, the **My vehicles** group with a coloured type dot, the registration
 * and either a radio or a `Resume ›` link per row, then the separate **Temporarily assigned to
 * me** `FLEET` group.
 *
 * @param onAddVehicle Navigates to the wizard. **Which vehicle it opens on is decided before the
 *   navigation, not after it** — ＋ and 026a's *"Yes, onboard ›"* call `startNewVehicle` for a
 *   Step 1/4 (US-2.27 as amended), a row's *Resume ›* calls `resumeOnboarding` for that row. The
 *   route carries no arguments, so `VehicleOnboardingSession` carries the intent instead.
 * @param onOpenStatus Opens SCR-DA-006 for a vehicle whose documents are being verified.
 * @param onOpenDocuments Opens SCR-DA-029a, where the documents themselves can be looked at
 *   (Δ MCS-28). Distinct from [onOpenStatus], which answers "has an officer checked this yet" —
 *   this one answers "show me the certificate", which is the question asked at a checkpoint.
 * @param onBack Leaves for the Menu tab.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun VehiclesScreen(
    onAddVehicle: () -> Unit,
    onOpenStatus: () -> Unit,
    onOpenDocuments: () -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: VehiclesViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.vehicles_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
                actions = {
                    IconButton(
                        onClick = {
                            viewModel.startNewVehicle()
                            onAddVehicle()
                        },
                    ) {
                        Icon(
                            imageVector = Icons.Outlined.Add,
                            contentDescription = stringResource(R.string.vehicles_add),
                        )
                    }
                },
            )
        },
    ) { insets ->
        Column(modifier = Modifier.padding(insets).fillMaxSize()) {
            if (state.loading) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }

            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                item {
                    NoticeCard(accent = MaterialTheme.colorScheme.primary, icon = Icons.Outlined.Info) {
                        Text(
                            text = stringResource(R.string.vehicles_one_live),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }

                state.error?.let { message ->
                    item {
                        Text(
                            text = stringResource(message),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.error,
                        )
                    }
                }

                if (state.isEmpty) {
                    item { EmptyVehicles() }
                }

                if (state.owned.isNotEmpty()) {
                    item { SectionLabel(text = stringResource(R.string.vehicles_mine_label)) }
                    items(state.owned, key = { it.vehicleId }) { row ->
                        VehicleCard(
                            row = row,
                            active = row.vehicleId == state.activeVehicleId,
                            onSelect = { viewModel.select(row) },
                            onResume = {
                                viewModel.resumeOnboarding(row)
                                onAddVehicle()
                            },
                            onOpenStatus = {
                                viewModel.open(row)
                                onOpenStatus()
                            },
                            onOpenDocuments = onOpenDocuments,
                            onDeactivate = { viewModel.confirmDeactivate(row) },
                        )
                    }
                }

                if (state.assigned.isNotEmpty()) {
                    item {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(top = MageRideTheme.spacing.xs),
                            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            SectionLabel(
                                text = stringResource(R.string.vehicles_assigned_label),
                                modifier = Modifier.weight(1f),
                            )
                            StatusPill(label = stringResource(R.string.vehicles_fleet_badge), tone = StatusTone.NEUTRAL)
                        }
                    }
                    items(state.assigned, key = { it.vehicleId }) { row ->
                        VehicleCard(
                            row = row,
                            active = row.vehicleId == state.activeVehicleId,
                            onSelect = { viewModel.select(row) },
                            onResume = null,
                            onOpenStatus = null,
                            // An assigned fleet vehicle's documents are still the driver's to show
                            // at a checkpoint — the server decides entitlement, and it includes
                            // assignments (US-13.9).
                            onOpenDocuments = onOpenDocuments,
                            onDeactivate = null,
                        )
                    }
                }
            }
        }
    }

    if (state.onboardPromptVisible) {
        OnboardModeCDialog(
            onConfirm = {
                viewModel.dismissOnboardPrompt()
                viewModel.startNewVehicle()
                onAddVehicle()
            },
            onDismiss = viewModel::dismissOnboardPrompt,
        )
    }

    state.deactivating?.let { row ->
        DeactivateDialog(
            registrationNumber = row.summary.registrationNumber,
            onConfirm = viewModel::deactivate,
            onDismiss = viewModel::cancelDeactivate,
        )
    }
}

/**
 * One vehicle row.
 *
 * The selection control is the wireframe's `◉`/`○`, and it is **only live on a vehicle that can go
 * live** (US-9.6): an Incomplete Mode C vehicle shows `Resume ›` in its place, because selecting a
 * vehicle that cannot be dispatched is not a state the driver should be able to reach.
 */
@Suppress("LongParameterList") // A row's data, its selection state and its four actions.
@Composable
private fun VehicleCard(
    row: VehicleRow,
    active: Boolean,
    onSelect: () -> Unit,
    onResume: (() -> Unit)?,
    onOpenStatus: (() -> Unit)?,
    onOpenDocuments: (() -> Unit)?,
    onDeactivate: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(
            containerColor = if (active) {
                MaterialTheme.colorScheme.primaryContainer
            } else {
                MaterialTheme.colorScheme.surfaceVariant
            },
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(ControlTokens.StatusDot)
                        .background(
                            color = MageRideTheme.vehicle.forType(row.summary.vehicleType),
                            shape = CircleShape,
                        ),
                )
                Column(
                    modifier = Modifier
                        .weight(1f)
                        .padding(horizontal = MageRideTheme.spacing.xs),
                    verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
                ) {
                    Text(
                        text = "${stringResource(row.summary.vehicleType.labelRes())} · " +
                            row.summary.registrationNumber,
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                    )

                    // Δ MCS-02 — the wireframe's "Lanka Fleet (Pvt) Ltd · until 30 Jun". Absent
                    // on an owned vehicle, and the date is absent again on an open-ended
                    // assignment, which is a real state rather than a missing value.
                    row.assignmentCaption()?.let { caption ->
                        Text(
                            text = caption,
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }

                    VehicleStatusPill(row = row, active = active)
                }

                if (row.canGoLive) {
                    RadioButton(selected = active, onClick = onSelect)
                } else if (onResume != null && row.isIncomplete) {
                    TextButton(onClick = onResume) {
                        Text(
                            text = stringResource(R.string.vehicles_resume),
                            style = MaterialTheme.typography.labelLarge,
                        )
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                if (onOpenStatus != null && row.summary.isOnboardable) {
                    TextButton(onClick = onOpenStatus) {
                        Text(
                            text = stringResource(R.string.vehicles_view_status),
                            style = MaterialTheme.typography.labelLarge,
                        )
                    }
                }
                // Δ MCS-28 — the documents themselves, not their verification state. Offered on
                // every card: a driver at a roadside is asked for the certificate of whichever
                // vehicle they are in, approved or not.
                if (onOpenDocuments != null) {
                    TextButton(onClick = onOpenDocuments) {
                        Text(
                            text = stringResource(R.string.documents_vehicle_action),
                            style = MaterialTheme.typography.labelLarge,
                        )
                    }
                }
                if (onDeactivate != null) {
                    TextButton(onClick = onDeactivate) {
                        Text(
                            text = stringResource(R.string.vehicles_deactivate),
                            style = MaterialTheme.typography.labelLarge,
                            color = MaterialTheme.colorScheme.error,
                        )
                    }
                }
            }
        }
    }
}

/**
 * The wireframe's per-row status: *"Active now"*, *"Approved"*, *"Incomplete · Step 3 of 4"*.
 *
 * The step number is what makes Incomplete actionable rather than a scold — a driver who knows
 * they are on Step 3 of 4 finishes; one told only that something is missing goes to support.
 */
@Composable
private fun VehicleStatusPill(row: VehicleRow, active: Boolean, modifier: Modifier = Modifier) {
    val stepNumber = row.nextStepNumber
    when {
        active -> StatusPill(
            label = stringResource(R.string.vehicles_active_now),
            tone = StatusTone.DONE,
            modifier = modifier,
        )

        row.isIncomplete && stepNumber != null -> StatusPill(
            label = stringResource(
                R.string.vehicles_incomplete_step,
                stepNumber,
                OnboardingStep.entries.size,
            ),
            tone = StatusTone.PENDING,
            modifier = modifier,
        )

        row.isIncomplete -> StatusPill(
            label = stringResource(R.string.vehicles_incomplete),
            tone = StatusTone.PENDING,
            modifier = modifier,
        )

        else -> StatusPill(
            label = stringResource(row.summary.status.labelRes()),
            tone = if (row.summary.status == RegistrationStatus.APPROVED) StatusTone.DONE else StatusTone.PENDING,
            modifier = modifier,
        )
    }
}

/** The wireframe's 🚗 *"No vehicles yet"* block behind the 026a dialog. */
@Composable
private fun EmptyVehicles(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xl),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.vehicles_empty_title),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = stringResource(R.string.vehicles_empty_body),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** **SCR-DA-026a** — *"Onboard a standby vehicle?"*, an `AlertDialog` exactly as D2' §026a says. */
@Composable
private fun OnboardModeCDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.vehicles_onboard_prompt_title)) },
        text = { Text(text = stringResource(R.string.vehicles_onboard_prompt_body)) },
        confirmButton = {
            TextButton(onClick = onConfirm) { Text(text = stringResource(R.string.vehicles_onboard_prompt_yes)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.vehicles_onboard_prompt_not_now)) }
        },
    )
}

/** US-2.16's confirm. Deactivating releases the plate (D-37), so it is not a silent action. */
@Composable
private fun DeactivateDialog(registrationNumber: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.vehicles_deactivate_title)) },
        text = { Text(text = stringResource(R.string.vehicles_deactivate_body, registrationNumber)) },
        confirmButton = {
            TextButton(onClick = onConfirm) { Text(text = stringResource(R.string.vehicles_deactivate)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.action_dismiss)) }
        },
    )
}

/**
 * The wireframe's assignment caption — the fleet, and the date the loan ends (Δ MCS-02, US-13.9).
 *
 * `null` for an owned vehicle. The date goes through `BusinessCalendar` because D-38 gives the
 * platform one timezone for a business date and it is **Asia/Colombo**, not the handset's — a
 * driver in another zone must not read a different expiry from the one the fleet set.
 */
@Composable
private fun VehicleRow.assignmentCaption(): String? {
    val fleet = summary.fleetName
    val until = summary.assignedUntil

    return when {
        fleet == null -> null

        until == null -> fleet

        else -> stringResource(
            R.string.vehicles_assigned_until,
            fleet,
            BusinessCalendar.businessDate(until).toString(),
        )
    }
}

/** The registration status as the driver reads it (US-2.13). */
private fun RegistrationStatus.labelRes(): Int = when (this) {
    RegistrationStatus.PENDING -> R.string.vehicle_registration_pending
    RegistrationStatus.APPROVED -> R.string.vehicle_registration_approved
    RegistrationStatus.REJECTED -> R.string.vehicle_registration_rejected
    RegistrationStatus.DEACTIVATED -> R.string.vehicle_registration_deactivated
}
