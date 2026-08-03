package lk.mageride.driver.support

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
import androidx.compose.material.icons.outlined.AttachFile
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.capture.readImage
import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketEvent

/**
 * SCR-DA-033's three overlays, hosted together because at most one is ever up.
 *
 * The raise-ticket sheet is the wireframe's own SCR-DA-033a; the article and thread sheets are
 * US-16.1's *"+ detail"* and US-16.2's *"track ticket"*, which D2' names and the wireframe does not
 * draw a screen for. Both are `ModalBottomSheet` rather than routes for that reason — adding two
 * destinations the team-approved baseline has no frame for would be a deviation, and a sheet over
 * the list the driver tapped is the least of it.
 */
@Composable
internal fun SupportSheets(state: SupportState, viewModel: SupportViewModel) {
    when (state.sheet) {
        SupportSheet.RAISE_TICKET -> RaiseTicketSheet(state = state, viewModel = viewModel)

        SupportSheet.TICKET_THREAD -> TicketThreadSheet(
            ticket = state.ticket,
            onDismiss = viewModel::closeSheet,
        )

        SupportSheet.FAQ_ARTICLE -> ArticleSheet(
            title = state.article?.title,
            body = state.article?.body,
            onDismiss = viewModel::closeSheet,
        )

        null -> Unit
    }
}

/**
 * **SCR-DA-033a · raise a ticket** — an M3 modal bottom sheet (US-16.2).
 *
 * The wireframe's four controls, in its order: **Issue description**, the **Related trip**
 * dropdown, *"📎 Attach a screenshot"* and **Submit ticket**. The title changes with the category —
 * the same sheet is the daily-fee refund request (US-9.23) — because both post the same operation
 * and only `category` differs.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RaiseTicketSheet(state: SupportState, viewModel: SupportViewModel) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // The system photo picker: no READ_MEDIA_IMAGES, no storage permission on any API level this
    // app supports, and the grant dies with the pick — the same contract SCR-DA-005's gallery
    // fallback uses. `readImage` takes the bytes now, because the Uri does not outlive the process.
    val pickScreenshot = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            scope.launch { viewModel.onScreenshotPicked(readImage(context, uri, SCREENSHOT_FILE_NAME)) }
        }
    }

    ModalBottomSheet(onDismissRequest = viewModel::closeSheet, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(
                    if (state.isRefundRequest) R.string.support_refund_title else R.string.support_raise_ticket,
                ),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            OutlinedTextField(
                value = state.description,
                onValueChange = viewModel::onDescriptionChange,
                modifier = Modifier.fillMaxWidth(),
                label = { Text(text = stringResource(R.string.support_issue_label)) },
                placeholder = { Text(text = stringResource(R.string.support_issue_hint)) },
                minLines = MIN_DESCRIPTION_LINES,
                singleLine = false,
            )

            SectionLabel(text = stringResource(R.string.support_related_trip))
            TripDropdown(state = state, onSelect = viewModel::onTripSelected)

            OutlinedButton(onClick = { pickScreenshot.launch(imageOnly()) }, modifier = Modifier.fillMaxWidth()) {
                Icon(imageVector = Icons.Outlined.AttachFile, contentDescription = null)
                Text(
                    text = stringResource(
                        if (state.screenshot == null) {
                            R.string.support_attach
                        } else {
                            R.string.support_attached
                        },
                    ),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }

            MageRideCta(
                label = stringResource(R.string.support_submit_ticket),
                onClick = viewModel::submit,
                enabled = state.canSubmit,
                loading = state.submitting,
            )
        }
    }
}

/**
 * The wireframe's *"DRV-22011-0617 · Galle Face → Nugegoda ▾"*.
 *
 * The id in the wireframe is a driver-and-date composite this platform does not mint (the same
 * finding C074 recorded about `DRV-22011`), so the row is the **route and the day** — which is what
 * a driver recognises a trip by, and what the support agent will search on. Optional: a fee charged
 * when the app crashed on Go Online is not about a trip at all.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TripDropdown(state: SupportState, onSelect: (String?) -> Unit, modifier: Modifier = Modifier) {
    var expanded by remember { mutableStateOf(false) }
    val selected = state.trips.firstOrNull { it.tripId == state.tripId }

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
                // `menuAnchor()` is deprecated there. See apps/driver-android/CLAUDE.md.
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
                        onSelect(trip.tripId)
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
                StatusPill(
                    label = stringResource(SupportLabels.status(ticket.status)),
                    tone = SupportLabels.tone(ticket.status),
                )
            }

            Text(
                text = ticket.description,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            // Oldest first, as the contract sends it: a thread read bottom-up is a thread nobody
            // reads. `assigned` entries are skipped — who is handling a complaint is not the
            // driver's to see, which is the contract's own rule rather than this screen's.
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

/** One FAQ article's body (US-16.1). Server-rendered in the driver's own language (D-26). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ArticleSheet(title: String?, body: String?, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.md)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = title ?: stringResource(R.string.support_loading),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
                textAlign = TextAlign.Start,
            )
            body?.let {
                // Markdown as written. The app ships no renderer and support-svc's articles are
                // short prose; a half-implemented one that swallowed `#` would be worse than none.
                Text(
                    text = it,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * *"12 Jun · Galle Face → Nugegoda"*.
 *
 * The date is read in **Colombo** through C072's `ScheduleLabels`, not in the handset's zone
 * (D-38): a driver naming yesterday's trip to support must name the same day support sees.
 */
@Composable
private fun tripLabel(trip: TripSummary): String {
    val locale = LocalConfiguration.current.locales[0]
    val route = listOfNotNull(trip.pickup?.address, trip.dropoff?.address)
        .joinToString(" ${ScheduleLabels.ROUTE_ARROW} ")
        .ifBlank { stringResource(R.string.support_trip_unnamed) }

    return "${ScheduleLabels.date(trip.startedAt, locale)} ${Symbols.DOT} $route"
}

private fun imageOnly() = PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)

/** What the attachment is called in `docs.uploads`. Not user-facing. */
private const val SCREENSHOT_FILE_NAME = "support-screenshot.jpg"

/** The wireframe's `min-height:72px` description box, as lines. */
private const val MIN_DESCRIPTION_LINES = 3
