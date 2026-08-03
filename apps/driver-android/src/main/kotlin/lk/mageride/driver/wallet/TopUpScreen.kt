package lk.mageride.driver.wallet

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.domain.wallet.TopupMethod
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-022 · top up wallet** (US-9.18, US-9.19, AL-05, AL-15).
 *
 * The wireframe, top to bottom: a `‹ Top Up` app bar, the three method chips, the amount field, the
 * voucher ladder with its DB-configured discounts, the note that explains where the discount lives,
 * and the CTA that reads *"Pay Rs 1,800 · get Rs 2,000"* when a tile is selected.
 *
 * **There is no bank-transfer tile and there is nowhere to add one** (AL-05). Read
 * [TopUpViewModel]'s KDoc before touching the voucher path — buying one is a purchase on
 * subscription-svc, not a top-up of the discounted price.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun TopUpScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: TopUpViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    // The OnePay page and a bank app are other apps: the only signal that a driver came back from
    // one is the lifecycle. D6' §7.1's window is resolved from here.
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { viewModel.resumeFromGateway() }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.wallet_top_up_title)) },
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
            if (state.loading || state.submitting) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            if (state.awaitingGateway) {
                DashboardBanner(
                    text = stringResource(R.string.wallet_topup_pending),
                    accent = MaterialTheme.colorScheme.secondary,
                )
            }
            state.pending?.takeIf { it.timedOut }?.let {
                DashboardBanner(
                    text = stringResource(R.string.wallet_topup_slow),
                    accent = MageRideTheme.status.warning,
                )
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                SectionLabel(text = stringResource(R.string.wallet_method))
                MethodChips(selected = state.method, onSelect = viewModel::selectMethod)

                LabelledTextField(
                    label = stringResource(R.string.wallet_amount_label),
                    value = state.amount,
                    onValueChange = viewModel::onAmountChange,
                    prefix = MoneyFormat.PREFIX,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                )

                SectionLabel(text = stringResource(R.string.wallet_voucher_label))
                VoucherTiles(state = state, onSelect = viewModel::selectVoucher)
                VoucherNote()

                PayCta(state = state, onPay = viewModel::pay)
            }
        }
    }

    state.fallbackQr?.let { payload ->
        FallbackDialog(payload = payload, onDismiss = viewModel::dismissFallback)
    }
    state.receipt?.let { receipt ->
        ReceiptDialog(receipt = receipt, onDismiss = viewModel::dismissReceipt)
    }
}

/**
 * The wireframe's `💳 Card · OnePay · LankaQR` chips.
 *
 * Three, because [TopupMethod] has three entries and no fourth can be added (AL-05). Card and
 * OnePay wallet are separate tiles over one endpoint — the choice between them is made on OnePay's
 * hosted page, and a tile still has to know which one it is.
 */
@Composable
private fun MethodChips(selected: TopupMethod, onSelect: (TopupMethod) -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        TopupMethod.entries.forEach { method ->
            FilterChip(
                selected = method == selected,
                onClick = { onSelect(method) },
                label = { Text(text = stringResource(method.labelRes())) },
            )
        }
    }
}

/**
 * The bulk-voucher ladder (US-9.19).
 *
 * Empty when the tier table has not been read or Finance has withdrawn every denomination —
 * `VoucherCatalogue` deliberately ships no default ladder, because a rate nobody set is worse than
 * no offer at all.
 */
@Composable
private fun VoucherTiles(state: TopUpState, onSelect: (Long) -> Unit, modifier: Modifier = Modifier) {
    if (state.vouchers.isEmpty()) {
        Text(
            modifier = modifier,
            text = stringResource(R.string.wallet_voucher_empty),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        return
    }

    FlowRow(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        state.vouchers.forEach { quote ->
            FilterChip(
                selected = quote.denomination.amountMinor == state.voucherDenominationMinor,
                onClick = { onSelect(quote.denomination.amountMinor) },
                label = {
                    Text(
                        text = stringResource(
                            R.string.wallet_voucher_tile,
                            MoneyFormat.rupees(quote.denomination),
                            MoneyFormat.percentOfBps(quote.discountBps),
                        ),
                    )
                },
            )
        }
    }
}

/** The wireframe's explanatory card — where the discount lives, and what it does not apply to. */
@Composable
private fun VoucherNote(modifier: Modifier = Modifier) {
    NoticeCard(modifier = modifier, accent = MaterialTheme.colorScheme.secondary) {
        Text(
            text = stringResource(R.string.wallet_voucher_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * *"Pay Rs 1,800 · get Rs 2,000"*, or *"Pay Rs 2,000"* for a plain top-up.
 *
 * The two-part label appears **only** when a voucher tile is selected, because it is the only case
 * where what is paid and what is credited differ — and that gap is the whole of US-9.19.
 */
@Composable
private fun PayCta(state: TopUpState, onPay: () -> Unit, modifier: Modifier = Modifier) {
    val payable = state.payableMinor
    val credited = state.creditedMinor
    val label = when {
        payable == null -> stringResource(R.string.wallet_pay)

        credited != null && credited != payable -> stringResource(
            R.string.wallet_pay_and_get,
            MoneyFormat.rupees(payable),
            MoneyFormat.rupees(credited),
        )

        else -> stringResource(R.string.wallet_pay_amount, MoneyFormat.rupees(payable))
    }

    MageRideCta(
        modifier = modifier,
        label = label,
        onClick = onPay,
        enabled = state.canPay,
        loading = state.submitting,
    )
}

/** AL-15's fallback: the payload rendered for a bank app on another device to scan. */
@Composable
private fun FallbackDialog(payload: String, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.wallet_lankaqr_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                Text(
                    text = stringResource(R.string.wallet_lankaqr_body),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                LankaQrCode(payload = payload)
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.action_close)) }
        },
    )
}

/**
 * The wireframe's *"Success → receipt"*.
 *
 * A voucher purchase says the credit is on its way rather than that it has landed — see
 * [TopUpReceipt.settled], and [TopUpRepository] for why there is nothing to poll.
 */
@Composable
private fun ReceiptDialog(receipt: TopUpReceipt, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.wallet_receipt_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
                Text(
                    text = stringResource(R.string.wallet_receipt_paid, MoneyFormat.rupees(receipt.paidMinor)),
                    style = MaterialTheme.typography.bodyMedium,
                )
                Text(
                    text = stringResource(
                        R.string.wallet_receipt_credited,
                        MoneyFormat.rupees(receipt.creditedMinor),
                    ),
                    style = MaterialTheme.typography.titleMedium,
                    color = MageRideTheme.status.success,
                )
                if (!receipt.settled) {
                    Text(
                        text = stringResource(R.string.wallet_receipt_pending),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(text = stringResource(R.string.action_close)) }
        },
    )
}
