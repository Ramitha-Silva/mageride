package lk.mageride.driver.tracker

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material.icons.outlined.SatelliteAlt
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.ui.vehicleLabel
import lk.mageride.shared.data.models.registry.VehicleSummary
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-027 · GPS tracker pairing** (US-3.1/3.2, US-3.21–3.23, T-02/T-09).
 *
 * The wireframe, top to bottom: a `‹ Pair GPS tracker` app bar, the **Vehicle** selector, the
 * **IMEI** field, a two-button row (`▣ Scan device QR` · `Bind code`), the `Pair device` CTA, the
 * green *"✦ Hardware tracker behaviour"* card, and the fleet-CSV note.
 *
 * **Two of the wireframe's controls do less than it draws, and both are the wrapper's doing.**
 * `POST /v1/vehicles/{vehicleId}/device` — the only tracker route this app has — takes `{ imei }`
 * and nothing else, while provisioning-svc's own bind takes `method: [manual, qr, admin_code]` and
 * a `bindCode`. So **Bind code** has nothing to send and is drawn disabled with the reason under
 * it, and a scanned IMEI reaches the server indistinguishable from a typed one. Both are recorded
 * as C074 spec gaps; see [TrackerRepository].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun TrackerPairingScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: TrackerPairingViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.tracker_title)) },
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
            // US-3.4 / T-08. A duplicate IMEI is not a failure to retry — the serial is already
            // active somewhere and provisioning-svc has quarantined it — so it gets its own copy
            // rather than the generic "try again".
            if (state.quarantined) {
                DashboardBanner(
                    text = stringResource(R.string.tracker_quarantined),
                    accent = MaterialTheme.colorScheme.error,
                )
            } else {
                state.error?.let { message ->
                    DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
                }
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                VehicleSelector(
                    vehicles = state.vehicles,
                    selected = state.selected,
                    onSelect = viewModel::selectVehicle,
                )

                state.binding?.let { binding -> PairedConfirmation(binding = binding) }

                LabelledTextField(
                    label = stringResource(R.string.tracker_imei_label),
                    value = state.imei,
                    onValueChange = viewModel::onImeiChange,
                    isError = state.imeiRejected,
                    supporting = stringResource(
                        if (state.imeiRejected) R.string.tracker_imei_invalid else R.string.tracker_imei_help,
                    ),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                )

                EntryMethodRow(enabled = !state.isPaired, onScan = viewModel::startScan)

                MageRideCta(
                    label = stringResource(R.string.tracker_pair_action),
                    onClick = viewModel::pair,
                    enabled = state.canPair,
                    loading = state.pairing,
                )

                HardwareBehaviourCard()
                FleetCsvCard()
            }
        }
    }

    if (state.scanning) {
        DeviceQrScannerDialog(onScanned = viewModel::onScanned, onDismiss = viewModel::cancelScan)
    }
}

/**
 * The wireframe's `Vehicle: [Three-wheeler ▾]`.
 *
 * Every vehicle the driver owns or has been assigned is offered, Mode A/B included: a tracker on a
 * fleet bus is precisely the US-3.22 case, and the app's Mode-C-only fence (AL-27) is about
 * *onboarding* a vehicle, not about pairing hardware to one somebody else onboarded.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun VehicleSelector(
    vehicles: List<VehicleSummary>,
    selected: VehicleSummary?,
    onSelect: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    var expanded by remember { mutableStateOf(false) }

    ExposedDropdownMenuBox(
        expanded = expanded,
        onExpandedChange = { expanded = it },
        modifier = modifier,
    ) {
        OutlinedTextField(
            value = selected?.let { vehicleLabel(it) }.orEmpty(),
            onValueChange = {},
            modifier = Modifier
                .fillMaxWidth()
                // `PrimaryNotEditable`: the field is the menu's anchor and is read-only, so
                // TalkBack announces a dropdown rather than a text box that refuses typing. The
                // no-argument `menuAnchor()` is deprecated in M3 1.4.
                .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
            readOnly = true,
            label = { Text(text = stringResource(R.string.tracker_vehicle_label)) },
            supportingText = if (vehicles.isEmpty()) {
                { Text(text = stringResource(R.string.tracker_no_vehicles)) }
            } else {
                null
            },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            shape = RoundedCornerShape(MageRideTheme.radius.md),
        )

        ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            vehicles.forEach { vehicle ->
                DropdownMenuItem(
                    text = { Text(text = vehicleLabel(vehicle)) },
                    onClick = {
                        onSelect(vehicle.vehicleId)
                        expanded = false
                    },
                )
            }
        }
    }
}

/**
 * D2' §SCR-DA-027's *"paired → cert-issued confirmation"*.
 *
 * The `201` from the bind **is** the credential: provisioning-svc mints the X.509 or the signed PSK
 * inside that call (T-02, D6' §4.2), so a `bindingId` in hand means one was issued. What this card
 * cannot show is the device's live health — `lastSeen`, `battery`, `signal` come from
 * `GET /v1/trackers/{imei}`, which has no client here (C074 spec gap).
 */
@Composable
private fun PairedConfirmation(binding: TrackerBinding, modifier: Modifier = Modifier) {
    NoticeCard(
        accent = MageRideTheme.status.success,
        modifier = modifier,
        icon = Icons.Outlined.SatelliteAlt,
        title = stringResource(R.string.tracker_paired_title),
    ) {
        Text(
            text = stringResource(R.string.tracker_paired_body, TrackerImei.grouped(binding.imei)),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** The wireframe's `[▣ Scan device QR] [Bind code]` row. */
@Composable
private fun EntryMethodRow(enabled: Boolean, onScan: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
            OutlinedButton(onClick = onScan, enabled = enabled, modifier = Modifier.weight(1f)) {
                Icon(
                    imageVector = Icons.Outlined.QrCodeScanner,
                    contentDescription = null,
                    modifier = Modifier.padding(end = MageRideTheme.spacing.xxs),
                )
                Text(text = stringResource(R.string.tracker_scan_action))
            }
            // Disabled, and the line underneath says why: the owner-facing wrapper carries no
            // `bindCode`, so there is nowhere for one to be sent.
            OutlinedButton(onClick = {}, enabled = false, modifier = Modifier.weight(1f)) {
                Text(text = stringResource(R.string.tracker_bind_code_action))
            }
        }
        Text(
            text = stringResource(R.string.tracker_bind_code_help),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * The wireframe's green *"✦ Hardware tracker behaviour"* card — and the C074 fence, in words.
 *
 * US-3.6: once the device is bound it is the single active publisher and the phone stops.
 * US-3.22/3.23: a Mode A/B tracker starts and ends journeys on ignition with no app involved, so
 * the dashboard is *already* at "Journey started" when the driver opens it. US-3.21: a Mode C
 * tracker's GPS is accepted only while the driver is Online.
 */
@Composable
private fun HardwareBehaviourCard(modifier: Modifier = Modifier) {
    NoticeCard(
        accent = MageRideTheme.status.success,
        modifier = modifier,
        icon = Icons.Outlined.SatelliteAlt,
        title = stringResource(R.string.tracker_behaviour_title),
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
            listOf(
                R.string.tracker_behaviour_publisher,
                R.string.tracker_behaviour_ignition,
                R.string.tracker_behaviour_mode_c,
            ).forEach { line ->
                Text(
                    text = stringResource(line),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * The wireframe's *"Fleet (5,000+ trackers)? Use the Admin Portal CSV upload ›"* (US-3.2, T-09).
 *
 * Drawn as a note rather than as a link: the bulk path is `POST /v1/fleets/{fleetId}/trackers/bulk`
 * on provisioning-svc and it is the **Admin Portal's**, and this app has no Admin Portal origin to
 * open — `DriverEnvironment` carries the gateway and the MQTT host, and D7' puts the portal on a
 * different one. A `›` that opened nothing would be worse than a sentence that says where to go.
 */
@Composable
private fun FleetCsvCard(modifier: Modifier = Modifier) {
    NoticeCard(accent = MaterialTheme.colorScheme.secondary, modifier = modifier) {
        Text(
            text = stringResource(R.string.tracker_fleet_csv),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
