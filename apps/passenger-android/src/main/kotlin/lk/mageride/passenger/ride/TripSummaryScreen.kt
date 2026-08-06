package lk.mageride.passenger.ride

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-018 — the receipt.
 *
 * The wireframe: `✕ Trip complete`, the total with how it settled, the fare breakdown, and
 * *"Rate your driver ★"*. An unsettled ride gets a **Pay now** CTA instead.
 *
 * **No tip row**, on the wireframe's own instruction — its state line reads *"(drop tip / India
 * charges)"*. The contract is ready (`InitiatePaymentRequest.tipMinor`, E-10) whenever that
 * reverses.
 */
@Composable
internal fun TripSummaryScreen(
    onClose: () -> Unit,
    onPay: () -> Unit,
    onRate: () -> Unit,
    model: TripSummaryViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onClose) {
                Icon(
                    imageVector = Icons.Filled.Close,
                    contentDescription = stringResource(R.string.action_close),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = stringResource(R.string.summary_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Text(
            text = state.totalMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
            modifier = Modifier.fillMaxWidth(),
            style = MaterialTheme.typography.displaySmall,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
        Text(
            text = if (state.settled) {
                stringResource(R.string.summary_settled)
            } else {
                stringResource(R.string.summary_unpaid)
            },
            modifier = Modifier.fillMaxWidth(),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )

        SectionLabel(text = stringResource(R.string.summary_breakdown))
        state.breakdown?.let { breakdown ->
            BreakdownRow(stringResource(R.string.summary_first_km), MoneyFormat.rupees(breakdown.firstKmMinor))
            BreakdownRow(stringResource(R.string.summary_per_km), MoneyFormat.rupees(breakdown.perKmMinor))
            BreakdownRow(
                stringResource(R.string.summary_distance),
                stringResource(R.string.summary_km, breakdown.distanceKm),
            )
        }
        BreakdownRow(
            stringResource(R.string.summary_total),
            state.totalMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
        )

        state.error?.let { InlineError(message = stringResource(it)) }

        if (!state.settled) {
            MageRideCta(label = stringResource(R.string.summary_pay_now), onClick = onPay)
        } else if (state.canRate) {
            OutlinedButton(onClick = onRate, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.summary_rate))
            }
        }
    }
}

@Composable
private fun BreakdownRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}
