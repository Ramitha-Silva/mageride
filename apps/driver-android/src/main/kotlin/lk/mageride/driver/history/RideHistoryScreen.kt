package lk.mageride.driver.history

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.query.TripSummary
import org.koin.androidx.compose.koinViewModel
import java.util.Locale

/**
 * **SCR-DA-030 · ride history + trip details + rate passenger** (US-8.8, US-18.2, AL-35).
 *
 * The wireframe: a `‹ Ride history` app bar over a list of trip cards — route and fare on the first
 * line, *"17 Jun · 8 km · fare to you Rs 480"* and either a **Rate ★** link or the stars already
 * given on the second — with the rate-passenger sheet over it.
 *
 * **AL-35's fence: rating opens a modal bottom sheet, not an inline card.** [RatePassengerSheet] is
 * that sheet; nothing on this screen expands in place.
 *
 * **"fare to you" is the fare.** D5' has no commission and AL-01 removed the last thing that could
 * have taken one — what the passenger paid is what the driver keeps, less the flat daily fee that
 * SCR-DA-021 accounts for separately. So the row's two amounts are the same number, which is what
 * the wireframe draws, and it is not a mistake in the sketch.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RideHistoryScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: RideHistoryViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val locale = LocalConfiguration.current.locales[0]

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.history_title)) },
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
                    text = stringResource(R.string.history_empty),
                    modifier = Modifier.padding(MageRideTheme.spacing.md),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                return@Column
            }

            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                items(items = state.trips, key = { it.tripId }) { trip ->
                    TripCard(trip = trip, locale = locale, onRate = { viewModel.openRating(trip.tripId) })
                }
            }
        }
    }

    RatePassengerSheet(state = state.rating, viewModel = viewModel)
}

/** One trip, as the wireframe draws it. */
@Composable
private fun TripCard(trip: HistoryTrip, locale: Locale, onRate: () -> Unit, modifier: Modifier = Modifier) {
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
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = tripRoute(trip.summary),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = trip.summary.fareMinor?.let(MoneyFormat::rupees) ?: Symbols.UNKNOWN,
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = tripDetailLine(trip = trip, locale = locale),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                when {
                    trip.rating != null -> Text(
                        text = stars(trip.rating),
                        style = MaterialTheme.typography.labelLarge,
                        color = MageRideTheme.status.warning,
                    )

                    trip.isRateable -> TextButton(onClick = onRate) {
                        Text(text = stringResource(R.string.history_rate_action) + " " + Symbols.STAR_FILLED)
                    }
                }
            }
        }
    }
}

/**
 * The wireframe's `Galle Face → Nugegoda`.
 *
 * `Place.address` is server-supplied display text and query-svc's trip list sends bare coordinates
 * with no address at all — the same absence `ScheduleLabels.route` documents for a scheduled ride —
 * so a trip with no address prints the dash rather than two decimal degrees no driver can read.
 */
internal fun tripRoute(summary: TripSummary): String {
    val pickup = summary.pickup?.address ?: Symbols.UNKNOWN
    val dropoff = summary.dropoff?.address ?: Symbols.UNKNOWN
    return "$pickup ${ScheduleLabels.ROUTE_ARROW} $dropoff"
}

/**
 * *"17 Jun · 8 km · fare to you Rs 480"* — assembled from what actually arrived.
 *
 * Each part is dropped rather than faked when its source is absent: `distanceKm` is missing while
 * the detail read is in flight and permanently on `GeometrySource.AGGREGATE_1M`, and a fare is
 * absent on a Mode A/B session, which has no fare at all.
 */
@Composable
private fun tripDetailLine(trip: HistoryTrip, locale: Locale): String {
    val parts = listOfNotNull(
        ScheduleLabels.date(trip.summary.startedAt, locale),
        trip.distanceKm?.let { MoneyFormat.distance(it * METRES_IN_KM) },
        trip.summary.fareMinor?.let { stringResource(R.string.history_fare_to_you, MoneyFormat.rupees(it)) },
    )
    return parts.joinToString(" ${Symbols.DOT} ")
}

/** `★★★★★` / `★★★☆☆` — a rating drawn the way the wireframe draws it. */
internal fun stars(given: Int): String {
    val filled = given.coerceIn(RatePassengerState.MIN_STARS, RatePassengerState.MAX_STARS)
    return Symbols.STAR_FILLED.repeat(filled) +
        Symbols.STAR_EMPTY.repeat(RatePassengerState.MAX_STARS - filled)
}

/** `TripDetail.distanceKm` is kilometres and `MoneyFormat.distance` takes metres. */
private const val METRES_IN_KM = 1_000.0
