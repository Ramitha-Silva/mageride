package lk.mageride.passenger.subscription

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ReceiptLong
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CreditCard
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.home.VehicleLabels
import lk.mageride.passenger.ui.DateFormat
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus

/**
 * SCR-PA-025 — "My subscriptions".
 *
 * The wireframe's card: the vehicle, a `B` badge, `Paid · Rs 6,000/mo · next due 6 Jul` or
 * `Free (office transport)`, then a status pill and the three controls — **💳 Pay** (Paid only),
 * **🧾** history, and a compact **✕** unsubscribe.
 *
 * **The card's title is the Vehicle ID, and that is a contract gap rather than a layout choice.**
 * The wireframe prints *"Office Van · MR-VEH-48213"*; `Subscription` carries `vehicleId` and
 * nothing else, and `GET /v1/vehicles/{id}` is *"visible to the owner, an assigned driver and
 * internal roles"* — a passenger reading it gets `403 not-owner`. So the id is what is drawn.
 * Recorded in the C082 handoff.
 *
 * **The ✕ opens a confirm.** Unsubscribing loses visibility of the vehicle immediately and
 * re-joining means a fresh request the owner has to accept (AL-25) — the tap is the last moment a
 * passenger can be told that.
 */
@Composable
internal fun SubscriptionsScreen(
    onBack: () -> Unit,
    onPay: (String) -> Unit,
    onHistory: (String) -> Unit,
    model: SubscriptionsViewModel,
) {
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
            SectionLabel(text = stringResource(R.string.subscriptions_title))
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
                text = stringResource(R.string.subscriptions_empty),
                modifier = Modifier
                    .fillMaxSize()
                    .padding(MageRideTheme.spacing.xl),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )

            else -> LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                items(state.cards, key = { it.subscription.subscriptionId }) { card ->
                    SwipeToUnsubscribe(onUnsubscribe = { model.confirmUnsubscribe(card) }) {
                        SubscriptionRow(
                            card = card,
                            leaving = state.leaving == card.subscription.subscriptionId,
                            onPay = { onPay(card.subscription.subscriptionId) },
                            onHistory = { onHistory(card.subscription.subscriptionId) },
                            onUnsubscribe = { model.confirmUnsubscribe(card) },
                        )
                    }
                }
            }
        }
    }

    state.confirming?.let { card ->
        UnsubscribeDialog(
            onConfirm = { model.unsubscribe(card) },
            onDismiss = model::dismissConfirm,
        )
    }
}

/**
 * The wireframe's *"Δ Android: swipe-to-unsubscribe also supported"*.
 *
 * **The swipe opens the confirm; it does not unsubscribe.** The card is snapped back the moment it
 * settles anywhere but home — losing sight of a school van because a thumb brushed a list is
 * exactly the accident AL-25 makes unrecoverable without the owner. The ✕ and the swipe land on
 * the same dialog, which is what keeps two affordances from having two consequences.
 *
 * Reset in a `LaunchedEffect` rather than through `confirmValueChange`, which material3 1.4
 * deprecated: vetoing a state change is now done by not offering the anchor, and there is no
 * anchor set that means *"let the gesture complete, then ask"*.
 */
@Composable
private fun SwipeToUnsubscribe(onUnsubscribe: () -> Unit, content: @Composable () -> Unit) {
    val state = rememberSwipeToDismissBoxState()

    LaunchedEffect(state.currentValue) {
        if (state.currentValue != SwipeToDismissBoxValue.Settled) {
            onUnsubscribe()
            state.reset()
        }
    }

    SwipeToDismissBox(
        state = state,
        backgroundContent = {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(
                        MaterialTheme.colorScheme.error.copy(alpha = PILL_TINT),
                        RoundedCornerShape(MageRideTheme.radius.md),
                    )
                    .padding(MageRideTheme.spacing.sm),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    imageVector = Icons.Filled.Close,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.error,
                )
            }
        },
        // End-to-start only: a left-to-right swipe on a list row is the platform's back gesture on
        // the edge of the screen, and competing with it is how a swipe-to-act control gets a
        // reputation for firing by itself.
        enableDismissFromStartToEnd = false,
        content = { content() },
    )
}

/** One vehicle. */
@Composable
private fun SubscriptionRow(
    card: SubscriptionCard,
    leaving: Boolean,
    onPay: () -> Unit,
    onHistory: () -> Unit,
    onUnsubscribe: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    // The Vehicle ID. See the screen's KDoc — there is no passenger-readable name.
                    text = card.subscription.vehicleId,
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = billingLine(card),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                // `B` — the same letter in all three scripts, which is why it is a constant.
                text = VehicleLabels.modeBadge(ServiceMode.B),
                modifier = Modifier
                    .background(MageRideTheme.mode.modeB, RoundedCornerShape(MageRideTheme.radius.sm))
                    .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onPrimary,
            )
        }

        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            StatusPill(card)

            Spacer(modifier = Modifier.weight(1f))

            // 💳 Pay and 🧾 are Paid-only: a Free vehicle collects nothing, so a payment screen
            // and a statement would both be empty by construction (US-23.2).
            if (card.paid) {
                TextButton(onClick = onPay, enabled = !leaving) {
                    Icon(
                        imageVector = Icons.Filled.CreditCard,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.subscriptions_pay),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
                IconButton(onClick = onHistory, enabled = !leaving) {
                    Icon(
                        imageVector = Icons.AutoMirrored.Filled.ReceiptLong,
                        contentDescription = stringResource(R.string.subscriptions_history),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            IconButton(onClick = onUnsubscribe, enabled = !leaving) {
                Icon(
                    imageVector = Icons.Filled.Close,
                    contentDescription = stringResource(R.string.subscriptions_unsubscribe),
                    tint = MaterialTheme.colorScheme.error,
                )
            }
        }
    }
}

/** `Paid` / `Payment due` / `Pending verification` / `Free`, in the wireframe's three tints. */
@Composable
private fun StatusPill(card: SubscriptionCard) {
    val (label, tint) = when {
        !card.paid -> R.string.subscriptions_free to MaterialTheme.colorScheme.onSurfaceVariant

        card.monthStatus == SubscriberMonthStatus.PAID ->
            R.string.subscriptions_status_paid to MageRideTheme.status.success

        card.monthStatus == SubscriberMonthStatus.PENDING_VERIFICATION ->
            R.string.subscriptions_status_pending to MageRideTheme.status.warning

        card.monthStatus == SubscriberMonthStatus.UNPAID ->
            R.string.subscriptions_status_due to MageRideTheme.status.warning

        // Still loading, or the statement could not be read. Neither is "Paid".
        else -> R.string.subscriptions_status_unknown to MaterialTheme.colorScheme.onSurfaceVariant
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
 * AL-25 spelled out before the tap that cannot be taken back.
 *
 * The wireframe draws only the compact ✕. What it does not draw is the consequence — the vehicle
 * disappears and coming back needs the owner's accept — and a passenger who unsubscribes from a
 * school van by mistake cannot fix it from this app.
 *
 * The second line is **US-23.12**, and it is a PDPA-shaped fact rather than a nicety: unsubscribing
 * does not remove the passenger from the fleet's records. Their row survives on the owner's roster,
 * muted, until the owner deletes it — so this is the moment to say that leaving the vehicle and
 * leaving the fleet's books are two different things.
 */
@Composable
private fun UnsubscribeDialog(onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.subscriptions_unsubscribe_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                Text(stringResource(R.string.subscriptions_unsubscribe_body))
                Text(
                    text = stringResource(R.string.subscriptions_muted_note),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(
                    text = stringResource(R.string.subscriptions_unsubscribe),
                    color = MaterialTheme.colorScheme.error,
                )
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) } },
    )
}

/**
 * `Paid · Rs 6,000/mo · next due 6 Jul`, or the Free vehicle's one line.
 *
 * The leading word is the vehicle's **Service payment** classification (AL-51's rename of "Mode B
 * classification"), not the state of this month — that is the pill's job, and the two are
 * genuinely different: a Paid vehicle whose month is unpaid says `Paid` here and `Payment due`
 * there.
 */
@Composable
private fun billingLine(card: SubscriptionCard): String {
    val fare = card.fare ?: return stringResource(R.string.subscriptions_free_caption)
    val billing = stringResource(R.string.subscriptions_billing_paid)
    val amount = stringResource(R.string.subscriptions_per_month, MoneyFormat.rupees(fare))
    val due = card.subscription.nextDue
        ?.let { stringResource(R.string.subscriptions_next_due, DateFormat.dayMonth(it)) }
    return listOfNotNull(billing, amount, due).joinToString(SEPARATOR)
}

/** The wireframe's `·` between the fare and the due date. Punctuation, not copy. */
private const val SEPARATOR = " · "

/** The wireframe's pills are a wash of their own colour, not a solid fill. */
private const val PILL_TINT = 0.16f
