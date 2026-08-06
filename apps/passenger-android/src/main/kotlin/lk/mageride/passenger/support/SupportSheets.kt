package lk.mageride.passenger.support

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import kotlinx.coroutines.launch
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.DateFormat
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketEvent
import lk.mageride.shared.util.BusinessCalendar

/**
 * SCR-PA-030's two overlays, hosted together because at most one is ever up.
 *
 * The raise-ticket sheet is the wireframe's own **SCR-PA-030a**. The thread sheet is D2'
 * §SCR-PA-030's *"ticket list/thread"*, which the wireframe draws no separate frame for — so it is a
 * `ModalBottomSheet` over the list the passenger tapped rather than a destination the team-approved
 * baseline has no picture of.
 */
@Composable
internal fun SupportSheets(state: SupportState, model: SupportViewModel) {
    when (state.sheet) {
        SupportSheet.RAISE_TICKET -> RaiseTicketSheet(state = state, model = model)
        SupportSheet.TICKET_THREAD -> TicketThreadSheet(ticket = state.ticket, onDismiss = model::closeSheet)
        null -> Unit
    }
}

/**
 * **SCR-PA-030a · raise a ticket** — an M3 modal bottom sheet (US-16.2).
 *
 * The wireframe's four controls, in its order: **Issue description** (multiline), the **Related
 * trip** dropdown, *"📎 Attach a screenshot"* and **Submit ticket**.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RaiseTicketSheet(state: SupportState, model: SupportViewModel) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // The system photo picker: no READ_MEDIA_IMAGES, no storage permission on any API level this
    // app supports, and the grant dies with the pick — which is exactly why `readScreenshot` takes
    // the bytes now rather than parking the Uri in state until Submit.
    val pickScreenshot = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            scope.launch { model.onScreenshotPicked(readScreenshot(context, uri)) }
        }
    }

    ModalBottomSheet(onDismissRequest = model::closeSheet, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.support_raise_ticket),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            OutlinedTextField(
                value = state.description,
                onValueChange = model::onDescriptionChange,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(text = stringResource(R.string.support_issue_label)) },
                placeholder = { Text(text = stringResource(R.string.support_issue_hint)) },
                minLines = MIN_DESCRIPTION_LINES,
                singleLine = false,
            )

            SectionLabel(text = stringResource(R.string.support_related_trip))
            TripDropdown(state = state, onSelect = model::onTripSelected)

            OutlinedButton(onClick = { pickScreenshot.launch(imageOnly()) }, modifier = Modifier.fillMaxWidth()) {
                Icon(imageVector = Icons.Filled.AttachFile, contentDescription = null)
                Text(
                    text = stringResource(
                        if (state.screenshot == null) R.string.support_attach else R.string.support_attached,
                    ),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }

            MageRideCta(
                label = stringResource(R.string.support_submit_ticket),
                onClick = model::submit,
                enabled = state.canSubmit,
                loading = state.submitting,
            )
        }
    }
}

/**
 * The wireframe's *"PAX-90431-0617 · Nugegoda → Galle Face ▾"*.
 *
 * The id in the wireframe is a passenger-and-date composite this platform does not mint (the same
 * finding C083 recorded about `PAX-90431`), so the row is the **day and the route** — which is what
 * a passenger recognises a trip by, and what the support agent will search on. Optional: a complaint
 * about the app itself is not about a trip at all.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TripDropdown(state: SupportState, onSelect: (String?) -> Unit, modifier: Modifier = Modifier) {
    var expanded by remember { mutableStateOf(false) }
    val selected = state.trips.firstOrNull { it.rideId == state.tripId }

    ExposedDropdownMenuBox(
        expanded = expanded,
        onExpandedChange = { expanded = !expanded },
        modifier = modifier.fillMaxWidth(),
    ) {
        OutlinedTextField(
            value = selected?.let { tripLabel(it) } ?: stringResource(R.string.support_trip_none),
            onValueChange = {},
            modifier = Modifier
                .fillMaxWidth()
                // `ExposedDropdownMenuAnchorType` is the Material3 1.4 spelling; the argument-less
                // `menuAnchor()` is deprecated there.
                .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
            readOnly = true,
            singleLine = true,
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
        )
        ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            DropdownMenuItem(
                text = { Text(text = stringResource(R.string.support_trip_none)) },
                onClick = {
                    onSelect(null)
                    expanded = false
                },
            )
            state.trips.forEach { trip ->
                DropdownMenuItem(
                    text = { Text(text = tripLabel(trip)) },
                    onClick = {
                        onSelect(trip.rideId)
                        expanded = false
                    },
                )
            }
        }
    }
}

/** One ticket and its whole conversation (US-16.2). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TicketThreadSheet(ticket: TicketDetail?, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            if (ticket == null) {
                Text(
                    text = stringResource(R.string.support_loading),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                return@Column
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = categoryLabel(ticket.category),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                StatusPill(status = ticket.status)
            }

            Text(
                text = ticket.description,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            // Oldest first, as the contract sends it: a thread read bottom-up is a thread nobody
            // reads. `assigned` entries are skipped — who is handling a complaint is not the
            // complainant's to see, which is the contract's own rule rather than this screen's.
            ticket.thread.forEach { event -> ThreadEntry(event = event) }
        }
    }
}

/** One `TicketEvent` — what happened, and the agent's words when there are any. */
@Composable
private fun ThreadEntry(event: TicketEvent, modifier: Modifier = Modifier) {
    val label = SupportLabels.event(event.kind) ?: return

    Column(modifier = modifier.fillMaxWidth()) {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        event.body?.takeIf(String::isNotBlank)?.let { body ->
            Text(
                text = body,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
    }
}

/**
 * *"12 Jun · Nugegoda → Galle Face"*.
 *
 * The date is read in **Colombo** through `BusinessCalendar.ZONE`, not in the handset's zone
 * (D-38): a passenger naming yesterday's trip to support must name the same day support sees.
 */
@Composable
private fun tripLabel(trip: RideHistoryRow): String {
    val route = listOfNotNull(trip.pickup?.address, trip.dropoff?.address)
        .joinToString(ARROW)
        .ifBlank { stringResource(R.string.support_trip_unnamed) }

    return "${DateFormat.dayMonth(BusinessCalendar.businessDate(trip.completedAt))} $SEPARATOR $route"
}

private fun imageOnly() = PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)

/** The wireframe's `min-height:72px` description box, as lines. */
private const val MIN_DESCRIPTION_LINES = 3

private const val ARROW = " → "
private const val SEPARATOR = "·"
