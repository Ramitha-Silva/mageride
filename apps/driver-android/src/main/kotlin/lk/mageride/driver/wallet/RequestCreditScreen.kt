package lk.mageride.driver.wallet

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.wallet.TransferRow
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-023 · request credit** (US-9.10, AL-01, AL-34).
 *
 * The wireframe, top to bottom: a `‹ Request credit` app bar, the card that explains what a Driver
 * ID is for and says there is **no QR scan**, the two fields, and the CTA.
 *
 * **One block is not in the wireframe: the outstanding requests underneath.** D2' §SCR-DA-023's
 * states name *"Requested → Awaiting driver approval"* as a state of this screen and the C073
 * deliverable asks for the pending-outgoing state; with nothing pending the screen is exactly the
 * wireframe's. Recorded in the C073 handoff.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RequestCreditScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: RequestCreditViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.wallet_request_title)) },
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
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            state.justRequested?.let { row ->
                DashboardBanner(
                    text = stringResource(R.string.wallet_request_awaiting, MoneyFormat.rupees(row.amountMinor)),
                    accent = MaterialTheme.colorScheme.secondary,
                )
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                NoticeCard(accent = MaterialTheme.colorScheme.secondary) {
                    Text(
                        text = stringResource(R.string.wallet_request_intro),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                LabelledTextField(
                    label = stringResource(R.string.wallet_driver_id_label),
                    value = state.holderId,
                    onValueChange = viewModel::onHolderIdChange,
                    isError = state.holderIdRejected,
                    supporting = stringResource(
                        if (state.holderIdRejected) {
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
                )

                PendingRequests(rows = state.outgoing)

                MageRideCta(
                    label = stringResource(R.string.wallet_request_action),
                    onClick = viewModel::request,
                    enabled = state.canRequest,
                    loading = state.submitting,
                )
            }
        }
    }
}

/** *"Awaiting driver approval"* — every request this driver has raised that is still open. */
@Composable
private fun PendingRequests(rows: List<TransferRow>, modifier: Modifier = Modifier) {
    if (rows.isEmpty()) return

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.wallet_request_outstanding))
        rows.forEach { row -> PendingRow(row = row) }
    }
}

/**
 * One outstanding request.
 *
 * `counterpartyName` is optional on the wire — wallet-svc sends it when registry-svc had one — so
 * the id is what identifies the row when it does not, which is also the only thing the requester
 * typed.
 */
@Composable
private fun PendingRow(row: TransferRow, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
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
                text = MoneyFormat.rupees(row.amountMinor),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        StatusPill(label = stringResource(row.status.labelRes()), tone = StatusTone.PENDING)
    }
}
