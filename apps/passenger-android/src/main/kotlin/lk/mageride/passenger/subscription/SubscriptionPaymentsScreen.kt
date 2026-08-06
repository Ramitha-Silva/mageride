package lk.mageride.passenger.subscription

import androidx.compose.foundation.background
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
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.DateFormat
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.util.BusinessCalendar

/**
 * SCR-PA-025b — "Payment history".
 *
 * The wireframe's row: the month, a status pill, then `6 Jun · LankaQR` on the left and the amount
 * on the right — with `Apr 2026 · Paid · cash` reading *"marked received by owner"*, because that
 * is literally who wrote the row (US-23.6).
 *
 * **Three statuses, and the middle one is the point.** *Paid* is settled, *Pending verification*
 * is an online transfer whose slip the owner has not confirmed yet (US-23.4), and *Paid · cash* is
 * the owner's own attestation. A payment sitting at `initiated` — a gateway hand-off the passenger
 * abandoned — is shown as unpaid rather than as pending: an initiated payment is not a promise of
 * money, which is `ModeBPaymentRules.monthStatus`'s rule and this screen's too.
 */
@Composable
internal fun SubscriptionPaymentsScreen(onBack: () -> Unit, model: SubscriptionPaymentsViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

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
                text = stringResource(R.string.subscription_payments_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
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
                text = stringResource(R.string.subscription_payments_empty),
                modifier = Modifier
                    .fillMaxSize()
                    .padding(MageRideTheme.spacing.xl),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )

            else -> LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                item { StatementHeader(state) }
                items(state.payments, key = { it.paymentId }) { payment -> PaymentRow(payment) }
            }
        }
    }
}

/** The wireframe's filled header: the vehicle and its monthly fare. */
@Composable
private fun StatementHeader(state: SubscriptionPaymentsState) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            // The Vehicle ID — the same gap SCR-PA-025's card has. See `SubscriptionsScreen`.
            text = state.subscription?.vehicleId.orEmpty(),
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
        state.fare?.let {
            Text(
                text = stringResource(R.string.subscriptions_per_month, MoneyFormat.rupees(it)),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** One month. */
@Composable
private fun PaymentRow(payment: SubscriptionPayment) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = DateFormat.monthYear(payment.periodMonth),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            StatusPill(payment)
        }

        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                // `6 Jun · LankaQR`. The date is when it settled where there is one, and the
                // period otherwise — an unpaid month has no `paidAt` and never will.
                text = DateFormat.dayMonth(payment.paidAt?.let(::businessDateOf) ?: payment.periodMonth) +
                    SEPARATOR + stringResource(SubscriptionRails.label(payment.method)),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                text = MoneyFormat.rupees(payment.money),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        // The wireframe's "marked received by owner" line, on the one method that has no other
        // evidence behind it: cash never touches a gateway (US-23.6).
        if (payment.method == SubscriptionPayMethod.CASH && payment.status == SubscriptionPaymentStatus.PAID) {
            Text(
                text = stringResource(R.string.subscription_cash_marked),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** `Paid` / `Paid · cash` / `Pending verification` / `Unpaid`. */
@Composable
private fun StatusPill(payment: SubscriptionPayment) {
    val (label, tint) = when (payment.status) {
        SubscriptionPaymentStatus.PAID -> when (payment.method) {
            SubscriptionPayMethod.CASH ->
                R.string.subscription_status_paid_cash to
                    MaterialTheme.colorScheme.onSurfaceVariant

            else -> R.string.subscription_status_paid to MageRideTheme.status.success
        }

        SubscriptionPaymentStatus.PENDING_VERIFICATION ->
            R.string.subscription_status_pending to MageRideTheme.status.warning

        // `initiated` is a hand-off nobody completed and `failed` is one that broke. Neither is
        // money the owner has, so neither may look like it.
        SubscriptionPaymentStatus.INITIATED, SubscriptionPaymentStatus.FAILED ->
            R.string.subscription_status_unpaid to MaterialTheme.colorScheme.error
    }

    Text(
        text = stringResource(label),
        modifier = Modifier
            .background(tint.copy(alpha = PILL_TINT), RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurface,
    )
}

/**
 * The Colombo calendar date an instant falls on (D-13/D-38).
 *
 * `paidAt` is an instant and the row prints a day, and the two disagree for five and a half hours
 * either side of midnight — so the conversion goes through `:shared`'s calendar rather than the
 * handset's zone.
 */
private fun businessDateOf(at: Timestamp): BusinessDate = BusinessCalendar.businessDate(at)

/** The wireframe's `·` between the date and the method. Punctuation, not copy. */
private const val SEPARATOR = " · "

/** The wireframe's pills are a wash of their own colour, not a solid fill. */
private const val PILL_TINT = 0.16f
