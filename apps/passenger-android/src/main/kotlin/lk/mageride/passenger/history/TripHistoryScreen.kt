package lk.mageride.passenger.history

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Call
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ride.CallChoice
import lk.mageride.passenger.ride.CallChooserSheet
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ride.RideHistoryRow

/**
 * SCR-PA-022 — Past / Scheduled / Packages.
 *
 * The wireframe's card: `Nugegoda → Galle Face · Paid · 17 Jun · 8.2 km · Sedan · Rs 850`, then
 * `🚗 K. Fernando · 📞 +94 77 123 4567` with a **Call**.
 *
 * **The number on the card is masked and the Call fetches the real one.**
 * `RideHistoryRow.driver.mobileMasked` is a `PhoneMasked` and `:shared`'s KDoc forbids parsing one
 * back into a dialable number; AL-48 put the clear number on `RideDetail.counterpartyPhone`. So the
 * card shows what the list gave and the tap costs one read — see [TripHistoryViewModel.call] and
 * the C081 handoff.
 *
 * **A trip cancelled before assignment shows no number and offers no Call.** There was no driver.
 */
@Composable
internal fun TripHistoryScreen(
    onOpenTrip: (String) -> Unit,
    onFreeCall: () -> Unit,
    choice: CallChoice,
    model: TripHistoryViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()
    var callOpen by remember { mutableStateOf(false) }

    LaunchedEffect(state.callFor) { if (state.callFor != null) callOpen = true }

    Column(modifier = Modifier.fillMaxSize()) {
        SectionLabel(
            text = stringResource(R.string.history_title),
            modifier = Modifier.padding(MageRideTheme.spacing.md),
        )

        TabRow(selectedTabIndex = state.tab.ordinal) {
            HistoryTab.entries.forEach { tab ->
                Tab(
                    selected = tab == state.tab,
                    onClick = { model.select(tab) },
                    text = { Text(stringResource(tab.label())) },
                )
            }
        }

        state.error?.let {
            InlineError(message = stringResource(it), modifier = Modifier.padding(MageRideTheme.spacing.md))
        }

        when {
            state.loading -> Column(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                CircularProgressIndicator(modifier = Modifier.size(ControlTokens.RowIcon))
            }

            state.empty -> Text(
                text = stringResource(state.tab.emptyLabel()),
                modifier = Modifier
                    .fillMaxSize()
                    .padding(MageRideTheme.spacing.xl),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )

            state.tab == HistoryTab.SCHEDULED -> LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(state.scheduled) { row -> ScheduledCard(row) }
            }

            else -> LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(state.visibleRides) { row ->
                    TripCard(row = row, onOpen = { onOpenTrip(row.rideId) }, onCall = { model.call(row) })
                }
            }
        }
    }

    val target = state.callFor
    if (callOpen && target != null) {
        CallChooserSheet(
            driverName = target.driverName,
            driverPhone = target.phone,
            choice = choice,
            onFreeCall = {
                callOpen = false
                model.onCallConsumed()
                onFreeCall()
            },
            onDialled = { },
            onDismiss = {
                callOpen = false
                model.onCallConsumed()
            },
        )
    }
}

/** One completed trip. */
@Composable
private fun TripCard(row: RideHistoryRow, onOpen: () -> Unit, onCall: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onOpen)
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = listOfNotNull(row.pickup?.address, row.dropoff?.address).joinToString(ARROW),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = row.fare?.amountMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Text(
            text = row.state.name,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )

        // US-24.4 / AL-48. Absent entirely for a cancelled-before-assignment trip: there was no
        // driver, and a contact row for one would be a fiction.
        if (row.hasReachableDriver()) {
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = row.driver?.name.orEmpty(),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        // The masked form, which is what the list carries. The Call resolves the
                        // real number — `PhoneMasked` must never be parsed back into one.
                        text = row.driver?.mobileMasked.orEmpty(),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(onClick = onCall) {
                    Icon(
                        imageVector = Icons.Filled.Call,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.ride_call),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
            }
        }
    }
}

/** One upcoming scheduled ride. */
@Composable
private fun ScheduledCard(row: ScheduledRideRow) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            text = listOfNotNull(row.pickupLabel, row.dropoffLabel).joinToString(ARROW),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = row.pickupTimeIso,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

private fun HistoryTab.label(): Int = when (this) {
    HistoryTab.PAST -> R.string.history_tab_past
    HistoryTab.SCHEDULED -> R.string.history_tab_scheduled
    HistoryTab.PACKAGES -> R.string.history_tab_packages
}

private fun HistoryTab.emptyLabel(): Int = when (this) {
    HistoryTab.PAST -> R.string.history_empty_past
    HistoryTab.SCHEDULED -> R.string.history_empty_scheduled
    HistoryTab.PACKAGES -> R.string.history_empty_packages
}

/** The wireframe's `→` between a pickup and a drop-off. Punctuation, not copy. */
private const val ARROW = " → "
