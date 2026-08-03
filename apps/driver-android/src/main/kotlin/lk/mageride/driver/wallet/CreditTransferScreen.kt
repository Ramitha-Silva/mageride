package lk.mageride.driver.wallet

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-024 · credit transfer + requests** (US-9.11/9.12, US-9.20/9.21, AL-01).
 *
 * The wireframe, top to bottom: a `‹ Credit transfer` app bar, the *"Incoming credit requests"*
 * label and its approve/decline cards, the *"or send directly"* divider, the two send fields, the
 * *"You send / Recipient gets"* card, and the CTA. The transfer history the deliverable asks for
 * follows underneath.
 *
 * **Nothing on this screen may render a commission line.** The card below the fields prints both
 * legs precisely so the exact-value rule is visible; see [CreditTransferViewModel].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun CreditTransferScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: CreditTransferViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.wallet_transfer_title)) },
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
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            if (state.loading) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            state.sent?.let { row ->
                DashboardBanner(
                    text = stringResource(R.string.wallet_transfer_sent_notice, MoneyFormat.rupees(row.amountMinor)),
                    accent = MageRideTheme.status.success,
                )
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                IncomingRequests(state = state, onApprove = viewModel::approve, onReject = viewModel::reject)

                HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                SectionLabel(text = stringResource(R.string.wallet_transfer_send_directly))

                LabelledTextField(
                    label = stringResource(R.string.wallet_driver_id_label),
                    value = state.recipientId,
                    onValueChange = viewModel::onRecipientIdChange,
                    isError = state.recipientIdRejected,
                    supporting = stringResource(
                        if (state.recipientIdRejected) {
                            R.string.wallet_driver_id_invalid
                        } else {
                            R.string.wallet_driver_id_help
                        },
                    ),
                )
                LabelledTextField(
                    label = stringResource(R.string.wallet_amount_label),
                    value = state.amount,
                    onValueChange = viewModel::onAmountChange,
                    prefix = MoneyFormat.PREFIX,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    supporting = viewModel.rejectionForSend()
                        ?.takeIf { state.amount.isNotBlank() }
                        ?.let { stringResource(it.messageRes()) },
                )

                ExactValueCard(state = state)

                MageRideCta(
                    label = stringResource(R.string.wallet_transfer_action),
                    onClick = viewModel::send,
                    enabled = viewModel.canSend(),
                    loading = state.submitting,
                )

                TransferHistory(rows = state.history)
            }
        }
    }
}

/** The approval inbox (US-9.11/9.12) — read, not pushed. See [CreditTransferRepository.pending]. */
@Composable
private fun IncomingRequests(
    state: CreditTransferState,
    onApprove: (String) -> Unit,
    onReject: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        SectionLabel(text = stringResource(R.string.wallet_transfer_incoming))

        if (state.incoming.isEmpty()) {
            Text(
                text = stringResource(R.string.wallet_transfer_incoming_empty),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            return@Column
        }

        state.incoming.forEach { row ->
            RequestCard(
                row = row,
                busy = state.busyTransferId == row.transferId,
                onApprove = { onApprove(row.transferId) },
                onReject = { onReject(row.transferId) },
            )
        }
    }
}

/**
 * One incoming request.
 *
 * The wireframe's subtitle reads *"Requested Rs 1,000 · Three-wheeler"*. **`TransferRow` carries no
 * vehicle** — `wallet.yaml` gives it a counterparty id, an optional name, an amount, a direction, a
 * status and a timestamp — so the vehicle is dropped rather than filled from a read that would be a
 * guess about which of the requester's vehicles they meant. Recorded in the C073 handoff.
 */
@Composable
private fun RequestCard(
    row: TransferRow,
    busy: Boolean,
    onApprove: () -> Unit,
    onReject: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Row(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = row.counterpartyName ?: row.counterpartyDriverId,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = stringResource(
                        R.string.wallet_transfer_requested,
                        MoneyFormat.rupees(row.amountMinor),
                    ),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            TextButton(onClick = onApprove, enabled = !busy) {
                Text(
                    text = stringResource(R.string.wallet_transfer_approve),
                    color = MageRideTheme.status.success,
                )
            }
            TextButton(onClick = onReject, enabled = !busy) {
                Text(
                    text = stringResource(R.string.wallet_transfer_decline),
                    color = MaterialTheme.colorScheme.error,
                )
            }
        }
    }
}

/**
 * The wireframe's *"You send Rs 1,000 / Recipient gets Rs 1,000 — exact value"*.
 *
 * Both figures come from `CreditTransferRules`, not from the input field twice: the rule is that
 * the two are equal (AL-01), and a card that printed the same variable twice would still look
 * right on the day somebody added a fee.
 */
@Composable
private fun ExactValueCard(state: CreditTransferState, modifier: Modifier = Modifier) {
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
            TransferRowLine(
                label = stringResource(R.string.wallet_transfer_you_send),
                value = state.debited?.let { MoneyFormat.rupees(it) } ?: MoneyFormat.EMPTY,
            )
            TransferRowLine(
                label = stringResource(R.string.wallet_transfer_recipient_gets),
                value = state.credited?.let { MoneyFormat.rupees(it) } ?: MoneyFormat.EMPTY,
                accent = true,
            )
            Text(
                text = stringResource(R.string.wallet_transfer_exact_value),
                style = MaterialTheme.typography.labelSmall,
                color = MageRideTheme.status.success,
            )
        }
    }
}

/** The wireframe's `kv`. */
@Composable
private fun TransferRowLine(label: String, value: String, modifier: Modifier = Modifier, accent: Boolean = false) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = if (accent) MageRideTheme.status.success else MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** Credit sent and received (US-9A.11). Sent is a debit, so its amount is drawn negative. */
@Composable
private fun TransferHistory(rows: List<TransferRow>, modifier: Modifier = Modifier) {
    if (rows.isEmpty()) return

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.wallet_transfer_history))
        rows.forEach { row ->
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = MageRideTheme.spacing.xxs),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = row.counterpartyName ?: row.counterpartyDriverId,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    Text(
                        text = stringResource(row.direction.labelRes()),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                StatusPill(
                    label = stringResource(row.status.labelRes()),
                    tone = when (row.status) {
                        TransferStatus.APPROVED -> StatusTone.DONE
                        TransferStatus.PENDING -> StatusTone.PENDING
                        TransferStatus.REJECTED -> StatusTone.NEUTRAL
                    },
                )
                Text(
                    text = MoneyFormat.rupees(row.amountMinor),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }
        }
    }
}
