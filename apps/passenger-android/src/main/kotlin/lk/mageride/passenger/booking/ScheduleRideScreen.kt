package lk.mageride.passenger.booking

import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Instant

/**
 * SCR-PA-013 — a ride in the future (US-6A.4, AL-36).
 *
 * The wireframe: `‹ Schedule ride`, a **Where to?** block whose second row is *"Select
 * destination…"*, an M3 `DatePicker`, an M3 `TimePicker`, the reminders line, and **Confirm
 * schedule** — *disabled until a destination is set*.
 *
 * **AL-36 is the whole screen.** The Definition of Done says *"Confirm on Schedule Ride is disabled
 * until a destination is chosen"*, the wireframe's state line says *"destination is mandatory
 * before scheduling"*, and [ScheduleRideState.canConfirm] is where both live. A time on its own is
 * not a booking: dispatch has nothing to post to the Job Board.
 *
 * @param onPickDestination The wireframe's *"Select destination…"* — the same picker as SCR-PA-008.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
@Suppress("LongMethod") // The wireframe's form: destination, date, time, reminders, CTA.
internal fun ScheduleRideScreen(
    onBack: () -> Unit,
    onPickDestination: () -> Unit,
    onScheduled: (String) -> Unit,
    model: ScheduleRideViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()
    var dateOpen by remember { mutableStateOf(false) }
    val dateState = rememberDatePickerState()
    val timeState = rememberTimePickerState(is24Hour = false)

    LaunchedEffect(state.scheduled) {
        state.scheduled?.let {
            onScheduled(it)
            model.onScheduleConsumed()
        }
    }

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
                text = stringResource(R.string.schedule_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            SectionLabel(text = stringResource(R.string.schedule_where_to))

            Text(
                text = state.pickup?.address ?: stringResource(R.string.search_current_location),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(onClick = onPickDestination)
                    .padding(vertical = MageRideTheme.spacing.xs),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = state.dropoff?.address ?: stringResource(R.string.schedule_select_destination),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyLarge,
                    color = if (state.dropoff == null) {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    } else {
                        MaterialTheme.colorScheme.onSurface
                    },
                )
                Icon(
                    imageVector = Icons.Filled.Search,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            SectionLabel(text = stringResource(R.string.schedule_when))
            OutlinedButton(onClick = { dateOpen = true }, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.schedule_pick_date))
            }
            TimePicker(state = timeState)

            // US-10.9 — set server-side off the same `dispatch.scheduled_rides` row and delivered
            // as pushes. The screen states the fact; it does not schedule anything itself.
            Text(
                text = stringResource(R.string.schedule_reminders),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            state.error?.let { InlineError(message = stringResource(it)) }

            MageRideCta(
                label = stringResource(R.string.schedule_confirm),
                onClick = {
                    dateState.selectedDateMillis?.let { day ->
                        model.setPickupTime(instantOf(day, timeState.hour, timeState.minute))
                    }
                    model.confirm()
                },
                // AL-36. `canConfirm` is false until a destination exists, so this is disabled
                // no matter what the date and time pickers say.
                enabled = state.dropoff != null && !state.saving,
                loading = state.saving,
                modifier = Modifier.padding(bottom = MageRideTheme.spacing.md),
            )
        }
    }

    if (dateOpen) {
        DatePickerDialog(
            onDismissRequest = { dateOpen = false },
            confirmButton = {
                TextButton(onClick = { dateOpen = false }) { Text(stringResource(R.string.action_ok)) }
            },
        ) {
            DatePicker(state = dateState)
        }
    }
}

/**
 * The two pickers' answers, combined into one instant.
 *
 * `DatePickerState.selectedDateMillis` is **UTC midnight** of the chosen day by M3's own contract,
 * and the `TimePicker` gives a wall-clock hour and minute. Adding them yields the instant the
 * passenger meant only if the handset is on the platform's timezone — which in Sri Lanka it is
 * (Asia/Colombo, D-13, the timezone every surcharge window is evaluated in). A handset roaming on
 * another zone would schedule against Colombo's clock, which is the right behaviour for a ride in
 * Colombo and the wrong one for a traveller booking ahead; recorded in the C079 handoff.
 */
private fun instantOf(utcMidnightMillis: Long, hour: Int, minute: Int): Instant =
    Instant.fromEpochMilliseconds(utcMidnightMillis) + hour.hours + minute.minutes - COLOMBO_OFFSET

/** Asia/Colombo is UTC+5:30 and has no daylight saving, which is what makes this a constant. */
private val COLOMBO_OFFSET = 5.hours + 30.minutes
