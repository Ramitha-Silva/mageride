package lk.mageride.driver.earnings

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
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.query.SessionEarning
import org.koin.androidx.compose.koinViewModel
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-020 · the earnings dashboard** (US-9.22, US-8.8).
 *
 * The wireframe, top to bottom: a `‹` app bar, the **Today / Week / Month** tab bar, the period's
 * net in display type, the bar chart, and the card that shows what the net is made of — fares
 * received, the daily fee deducted in `error`, and the bold **Net** line. The per-trip rows D2'
 * lists in its component table follow underneath.
 *
 * Read [EarningsViewModel] before adding a figure: the summary is query-svc's and is printed as
 * sent, and the payment-method split the deliverable asks for has no read behind it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun EarningsScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: EarningsViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.earnings_title)) },
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
            EarningsPeriodTabs(selected = state.period, onSelect = viewModel::select)

            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }

            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                item { PeriodTotal(state = state) }
                item { EarningsChart(buckets = state.buckets) }
                item { BreakdownCard(state = state) }

                if (state.isEmptyPeriod) {
                    item { EmptyPeriod() }
                } else {
                    item { SectionLabel(text = stringResource(R.string.earnings_trips)) }
                    items(items = state.trips, key = { it.tripId }) { trip -> TripRow(trip = trip) }
                }
            }
        }
    }
}

/** *"Today's earnings — Rs 3,180"*. The label names the selected window, not the day. */
@Composable
private fun PeriodTotal(state: EarningsState, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.earnings_net_for, stringResource(state.period.labelRes())),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = MoneyFormat.rupees(state.netMinor),
            style = MaterialTheme.typography.displaySmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * What the net is made of.
 *
 * The wireframe prints three lines — fares received, the daily fee, the net. Penalties (D5' §7.1)
 * and tips (E-10) are two more fields `EarningsSummary` carries, and each is shown **only when the
 * server sent a non-zero one**: a driver who has never been penalised should not have a penalty line
 * on their dashboard, and a driver who has must not have it folded silently into the net.
 */
@Composable
private fun BreakdownCard(state: EarningsState, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            MoneyRow(
                label = stringResource(R.string.earnings_fares_received, state.tripCount),
                value = MoneyFormat.rupees(state.grossMinor),
            )

            if (state.tipMinor > 0) {
                MoneyRow(
                    label = stringResource(R.string.earnings_tips),
                    value = MoneyFormat.rupees(state.tipMinor),
                )
            }

            if (state.dailyFeeMinor > 0) {
                MoneyRow(
                    label = stringResource(R.string.earnings_daily_fee),
                    value = MoneyFormat.rupees(-state.dailyFeeMinor),
                    accent = MaterialTheme.colorScheme.error,
                )
            }

            if (state.penaltyMinor > 0) {
                MoneyRow(
                    label = stringResource(R.string.earnings_penalties),
                    value = MoneyFormat.rupees(-state.penaltyMinor),
                    accent = MaterialTheme.colorScheme.error,
                )
            }

            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)

            MoneyRow(
                label = stringResource(R.string.earnings_net),
                value = MoneyFormat.rupees(state.netMinor),
                weight = FontWeight.Bold,
            )
        }
    }
}

/** The wireframe's `kv` — a label on the left and an amount on the right. */
@Composable
private fun MoneyRow(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    accent: Color? = null,
    weight: FontWeight? = null,
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            fontWeight = weight,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = accent ?: MaterialTheme.colorScheme.onSurface,
            fontWeight = weight,
        )
    }
}

/**
 * One completed trip (US-8.8).
 *
 * The time is the trip's **Colombo** end time, for the same reason the chart's buckets are — the
 * period the card totalled is a Colombo one.
 */
@OptIn(ExperimentalTime::class)
@Composable
private fun TripRow(trip: SessionEarning, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = ScheduleLabels.time(trip.endedAt),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = MoneyFormat.rupees(trip.grossMinor),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f),
            textAlign = TextAlign.End,
        )
        Text(
            text = MoneyFormat.rupees(trip.netMinor),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** D2' §SCR-DA-020's *"empty period"*. */
@Composable
private fun EmptyPeriod(modifier: Modifier = Modifier) {
    Text(
        text = stringResource(R.string.earnings_empty),
        style = MaterialTheme.typography.bodyLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = TextAlign.Center,
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.lg),
    )
}
