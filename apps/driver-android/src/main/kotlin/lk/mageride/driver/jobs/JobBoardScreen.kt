package lk.mageride.driver.jobs

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.EventNote
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.domain.dispatch.JobBoard
import org.koin.androidx.compose.koinViewModel
import java.util.Locale
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-017 · the Job Board** (US-6A.5, US-6A.8, D-06).
 *
 * The wireframe, top to bottom: an app bar reading *"Job Board"* with the catchment on the right,
 * then one card per future scheduled ride — the pickup time and route above the fare, the day,
 * distance and vehicle type below it, and on the right either the **Post intent** link or the
 * *"Intent posted ✓"* pill.
 *
 * **There is no accept button and there must not be one.** Acceptance happens at T-30 min on
 * SCR-DA-014, where the ride arrives as an ordinary offer; see [JobBoardViewModel].
 *
 * @param onOpenScheduled SCR-DA-018. The board is what a driver bids on and the upcoming list is
 *   what came of it, so the two are one tap apart. The wireframe draws SCR-DA-018 with a `‹` app
 *   bar and no screen that opens it — recorded as a wireframe gap in the C072 handoff.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun JobBoardScreen(onOpenScheduled: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: JobBoardViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.job_board_title)) },
                actions = {
                    Text(
                        text = stringResource(
                            R.string.job_board_radius,
                            MoneyFormat.radius(JobBoard.CATCHMENT_METRES),
                        ),
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    IconButton(onClick = onOpenScheduled) {
                        Icon(
                            imageVector = Icons.Outlined.EventNote,
                            contentDescription = stringResource(R.string.scheduled_title),
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

            when {
                state.loading -> BoardNotice(text = stringResource(R.string.job_board_loading), spinning = true)

                // US-6A.8 — a gate, not an error. Level 1 keeps immediate Mode C; what it loses is
                // this board, and the copy names the level that opens it again.
                state.gated == true -> BoardNotice(
                    text = stringResource(R.string.job_board_level_gate, state.minimumLevel),
                )

                // The level did not answer, so neither the gate nor the list is a truthful thing to
                // draw. Never the gate copy — that would tell a Level-3 driver they are Level 1.
                state.isUnavailable -> BoardNotice(text = stringResource(R.string.job_board_unavailable))

                state.isEmpty -> BoardNotice(
                    text = stringResource(
                        R.string.job_board_empty,
                        MoneyFormat.radius(JobBoard.CATCHMENT_METRES),
                    ),
                )

                else -> BoardList(state = state, onPostIntent = viewModel::postIntent)
            }
        }
    }
}

/** The board itself. Soonest pickup first — see [JobBoardViewModel] on what §3.7's ranking is. */
@Composable
private fun BoardList(state: JobBoardState, onPostIntent: (JobBoardRow) -> Unit, modifier: Modifier = Modifier) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        items(items = state.rows, key = { it.id }) { row ->
            JobCard(row = row, onPostIntent = { onPostIntent(row) })
        }
    }
}

/**
 * One board row.
 *
 * **The expired card fades rather than vanishing** (D2' §SCR-DA-017's *"card expire fade"*). The row
 * leaves the list a beat later, from the view model, so what a driver sees is a job going out of
 * reach rather than a tap that lost them one.
 */
@OptIn(ExperimentalTime::class)
@Composable
private fun JobCard(row: JobBoardRow, onPostIntent: () -> Unit, modifier: Modifier = Modifier) {
    val fade by animateFloatAsState(
        targetValue = if (row.expired) EXPIRED_ALPHA else 1f,
        label = "job-card-expire",
    )
    val locale = LocalContext.current.resources.configuration.locales[0]

    Card(
        modifier = modifier.fillMaxWidth().alpha(fade),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            Text(
                text = "${ScheduleLabels.time(row.ride.pickupTime)} · ${
                    ScheduleLabels.route(row.ride.pickup, row.ride.dropoff)
                }",
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = jobSubtitle(row, locale),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                JobAction(row = row, onPostIntent = onPostIntent)
            }
        }
    }
}

/** *"Tomorrow · 11.2 km · Sedan"* — the day, how far the pickup is, and the booked type. */
@OptIn(ExperimentalTime::class)
@Composable
private fun jobSubtitle(row: JobBoardRow, locale: Locale): String {
    val day = when (val label = ScheduleLabels.day(row.ride.pickupTime, Clock.System.now(), locale)) {
        DayLabel.Today -> stringResource(R.string.schedule_today)
        DayLabel.Tomorrow -> stringResource(R.string.schedule_tomorrow)
        is DayLabel.On -> label.text
    }
    val type = stringResource(row.ride.vehicleType.labelRes())
    val distance = row.ride.distanceM?.let { MoneyFormat.distance(it.toDouble()) }

    return listOfNotNull(day, distance, type).joinToString(SEPARATOR)
}

/**
 * The card's single control.
 *
 * **Post intent** while the window is open, the *"Intent posted ✓"* pill once this driver has bid,
 * and the expired pill once T-30 has passed. Nothing on this card accepts a ride.
 */
@Composable
private fun JobAction(row: JobBoardRow, onPostIntent: () -> Unit, modifier: Modifier = Modifier) {
    when {
        row.posting -> CircularProgressIndicator(
            modifier = modifier.size(ControlTokens.RowIcon),
            color = MaterialTheme.colorScheme.primary,
        )

        row.posted -> StatusPill(
            label = stringResource(R.string.job_board_intent_posted),
            tone = StatusTone.DONE,
            modifier = modifier,
        )

        row.expired -> StatusPill(
            label = stringResource(R.string.job_board_expired),
            tone = StatusTone.NEUTRAL,
            modifier = modifier,
        )

        else -> TextButton(onClick = onPostIntent, modifier = modifier, enabled = row.canPost) {
            Text(text = stringResource(R.string.job_board_post_intent), style = MaterialTheme.typography.labelLarge)
        }
    }
}

/** The gate, the empty board and the shimmer are one centred notice in three tones of copy. */
@Composable
private fun BoardNotice(text: String, modifier: Modifier = Modifier, spinning: Boolean = false) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.lg),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        if (spinning) {
            CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
        }
        Text(
            text = text,
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

/** What separates the three facts on a card's second line. A symbol, so it is not translated. */
private const val SEPARATOR = " · "

/** How much of an expired card is left once it has faded (D2' §SCR-DA-017's expire animation). */
private const val EXPIRED_ALPHA = 0.38f
