package lk.mageride.passenger.ride

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.fare.PaymentMethod

/**
 * SCR-PA-016 — how this ride gets paid.
 *
 * **Three rails and no surcharge anywhere**, which is the Definition-of-Done line
 * *"no surcharge is ever displayed on a ride"*. The +5 % the wireframe still draws belonged to
 * OnePay, and AL-57 retired OnePay as a ride method: a card fare could only ever land in
 * MageRide's own account, so card acceptance moved one step earlier to the **wallet top-up**.
 * AL-59 did the same to the platform-merchant LankaQR, which collapses into the driver's own QR.
 *
 * Every row therefore shows the **same total**. That is not a simplification — it is the point.
 *
 * @param onTopUp The wallet row's action when the balance is short. Rs 40 missing should be one tap
 *   from fixed, not a disabled row.
 */
@Composable
internal fun PaymentMethodScreen(
    onConfirmed: (PaymentMethod) -> Unit,
    onTopUp: () -> Unit,
    model: PaymentMethodViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.confirmed) {
        state.confirmed?.let {
            onConfirmed(it)
            model.onConfirmConsumed()
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        SectionLabel(text = stringResource(R.string.payment_title))

        state.methods.forEach { method ->
            RailRow(
                method = method,
                state = state,
                selected = method == state.chosen,
                onSelect = { model.choose(method) },
                onTopUp = onTopUp,
            )
        }

        state.error?.let { InlineError(message = stringResource(it)) }

        MageRideCta(
            label = stringResource(R.string.payment_confirm),
            onClick = model::confirm,
            enabled = !state.confirming,
        )
    }
}

/**
 * One rail.
 *
 * The amount on the right is the **same on every row** — there is no rail that costs more, because
 * the one that did was retired. A row that recomputed a total would be reintroducing the surcharge
 * this screen exists without.
 */
@Composable
private fun RailRow(
    method: PaymentMethod,
    state: PaymentMethodState,
    selected: Boolean,
    onSelect: () -> Unit,
    onTopUp: () -> Unit,
) {
    val short = method == PaymentMethod.WALLET && state.walletShort

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onSelect)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        RadioButton(selected = selected, onClick = onSelect)
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(PaymentRails.label(method)),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = when {
                    method == PaymentMethod.WALLET && state.walletBalanceMinor != null ->
                        stringResource(
                            R.string.payment_wallet_balance,
                            MoneyFormat.rupees(state.walletBalanceMinor),
                        )

                    else -> stringResource(PaymentRails.caption(method))
                },
                style = MaterialTheme.typography.labelSmall,
                color = if (short) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        if (short) {
            TextButton(onClick = onTopUp) { Text(stringResource(R.string.payment_top_up)) }
        } else {
            Text(
                // The same total on every row. See the KDoc.
                text = state.amountMinor?.let(MoneyFormat::rupees) ?: MoneyFormat.EMPTY,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
    }
}
