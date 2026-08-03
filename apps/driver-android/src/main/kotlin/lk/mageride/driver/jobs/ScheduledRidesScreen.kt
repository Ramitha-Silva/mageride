package lk.mageride.driver.jobs

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel
import java.util.Locale
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-018 · scheduled rides** (US-6A.15).
 *
 * The wireframe: a `‹` app bar over a list of cards, the imminent one outlined in `primary` with an
 * amber *"in 28 min"* pill and the note that its reminder has fired, the rest carrying a blue
 * *"Accepted"* pill and their vehicle type.
 *
 * **A no-show here costs a Driver Level** (US-6A.7), which is why the imminent card is the loud one.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ScheduledRidesScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: ScheduledRidesViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.scheduled_title)) },
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
        Column(modifier = Modifier.padding(insets).fillMaxSize()) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }

            if (state.isEmpty) {
                Text(
                    text = stringResource(R.string.scheduled_empty),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(MageRideTheme.spacing.lg),
                )
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(MageRideTheme.spacing.sm),
                    verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                ) {
                    items(items = state.rows, key = { it.id }) { row ->
                        ScheduledCard(row = row, onCancel = { viewModel.cancel(row) })
                    }
                }
            }
        }
    }
}

/**
 * One upcoming ride.
 *
 * The imminent card is outlined in `primary` — the wireframe's `border-color:var(--primary)` — which
 * is the only styling difference between the two rows it draws. Everything else about the card is
 * the same, deliberately: a driver scanning the list is looking for *which* is next, not for two
 * kinds of card.
 */
@OptIn(ExperimentalTime::class)
@Composable
private fun ScheduledCard(row: ScheduledRideRow, onCancel: () -> Unit, modifier: Modifier = Modifier) {
    val locale = LocalContext.current.resources.configuration.locales[0]

    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
        border = if (row.reminderFired) {
            BorderStroke(ControlTokens.BorderSelected, MaterialTheme.colorScheme.primary)
        } else {
            BorderStroke(ControlTokens.Border, MaterialTheme.colorScheme.outlineVariant)
        },
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = scheduledHeadline(row, locale),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.weight(1f),
                )
                ScheduledPill(row = row)
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = scheduledSubtitle(row),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                // Disabled from T-30: the ride exists by then and ride-svc's cancel — with its
                // penalty matrix — is the only door left (D5' §7).
                TextButton(onClick = onCancel, enabled = row.isScheduled && !row.cancelling) {
                    Text(
                        text = stringResource(R.string.scheduled_cancel),
                        style = MaterialTheme.typography.labelLarge,
                    )
                }
            }
        }
    }
}

/** *"Today 08:30 · Maharagama → Fort"*. */
@OptIn(ExperimentalTime::class)
@Composable
private fun scheduledHeadline(row: ScheduledRideRow, locale: Locale): String {
    val day = when (val label = ScheduleLabels.day(row.ride.pickupTime, Clock.System.now(), locale)) {
        DayLabel.Today -> stringResource(R.string.schedule_today)
        DayLabel.Tomorrow -> stringResource(R.string.schedule_tomorrow)
        is DayLabel.On -> label.text
    }
    val time = ScheduleLabels.time(row.ride.pickupTime)
    return "$day $time · ${ScheduleLabels.route(row.ride.pickup, row.ride.dropoff)}"
}

/**
 * *"Sedan · reminder fired"*.
 *
 * The wireframe also prints the fare (`Sedan · Rs 980 · reminder fired`). **`ScheduledRide` carries
 * no fare on any read** — see the C072 handoff — so the type and the reminder are what is left, and
 * a number is not invented to fill the gap.
 */
@Composable
private fun scheduledSubtitle(row: ScheduledRideRow): String {
    val type = stringResource(row.ride.vehicleType.labelRes())
    val reminder = if (row.reminderFired) stringResource(R.string.scheduled_reminder_fired) else null
    return listOfNotNull(type, reminder).joinToString(SEPARATOR)
}

/** The amber countdown while the reminder window is open, the blue *"Accepted"* pill before it. */
@Composable
private fun ScheduledPill(row: ScheduledRideRow, modifier: Modifier = Modifier) {
    if (row.reminderFired) {
        StatusPill(
            label = stringResource(R.string.scheduled_in_minutes, row.minutesToPickup),
            tone = StatusTone.PENDING,
            modifier = modifier,
        )
    } else {
        StatusPill(
            label = stringResource(R.string.scheduled_accepted),
            tone = StatusTone.INFO,
            modifier = modifier,
        )
    }
}

/** What separates the facts on a card's second line. A symbol, so it is not translated. */
private const val SEPARATOR = " · "
