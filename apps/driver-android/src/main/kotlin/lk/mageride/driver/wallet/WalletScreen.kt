package lk.mageride.driver.wallet

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.domain.wallet.WalletAlert
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-021 · wallet & fee status** (US-9.7, US-9.1, US-9.9).
 *
 * The wireframe, top to bottom: a `Wallet` app bar with no back arrow — this is bottom-nav tab 3,
 * not a pushed screen — the read-only balance in display type, the daily-fee card with its three
 * `kv` rows, and the two button rows that open the other four screens in this group.
 *
 * **One thing here is not in the wireframe: the *"Warn me below Rs 200"* row.** D2' §SCR-DA-021's
 * own states line calls the threshold *"driver-set"* and the C073 deliverable asks for the setting,
 * and there is nowhere else in this group it could live. [WalletPreferences] explains why it is
 * stored on the handset, and the dialog says so to the driver rather than implying a server-side
 * change. Recorded in the C073 handoff.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun WalletScreen(
    onTopUp: () -> Unit,
    onRequestCredit: () -> Unit,
    onTransferCredit: () -> Unit,
    onHistory: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: WalletViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    var editingThreshold by remember { mutableStateOf(false) }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = { TopAppBar(title = { Text(text = stringResource(R.string.wallet_title)) }) },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            if (state.loading) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            WalletNotice(state = state)

            Column(
                modifier = Modifier.padding(horizontal = MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                BalanceBlock(state = state)
                DailyFeeCard(state = state)
                ThresholdRow(state = state, onEdit = { editingThreshold = true })
                WalletActions(
                    onTopUp = onTopUp,
                    onRequestCredit = onRequestCredit,
                    onTransferCredit = onTransferCredit,
                    onHistory = onHistory,
                )
            }
        }
    }

    if (editingThreshold) {
        ThresholdDialog(
            current = state.threshold.amountMinor,
            onSave = { minor ->
                viewModel.setThreshold(minor)
                editingThreshold = false
            },
            onReset = {
                viewModel.clearThreshold()
                editingThreshold = false
            },
            onDismiss = { editingThreshold = false },
        )
    }
}

/**
 * The wireframe's centred *"Balance (read-only)"* and its display figure.
 *
 * `balance`, not `available`: US-9.7 calls this the balance and D2' marks it read-only. The
 * spendable figure is what every *decision* in this group is checked against — see
 * [WalletState.belowDayFee] and `CreditTransferRules.rejectionFor` — and it is shown as its own
 * line only when the two differ, because a driver with no accrued debt should not be reading two
 * numbers for one wallet.
 */
@Composable
private fun BalanceBlock(state: WalletState, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(top = MageRideTheme.spacing.sm),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.wallet_balance_label),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = state.balance?.let { MoneyFormat.rupees(it) } ?: MoneyFormat.EMPTY,
            style = MaterialTheme.typography.displaySmall,
            color = MaterialTheme.colorScheme.onSurface,
        )

        val debt = state.standing.standing?.outstandingDebt
        if (debt != null && debt.amountMinor > 0) {
            Text(
                text = stringResource(R.string.wallet_available_after_debt, MoneyFormat.rupees(debt)),
                style = MaterialTheme.typography.labelSmall,
                color = MageRideTheme.status.warning,
                textAlign = TextAlign.Center,
            )
        }
    }
}

/** The wireframe's `Daily fee` card — vehicle, rate, and today's status (US-9.1/9.7). */
@Composable
private fun DailyFeeCard(state: WalletState, modifier: Modifier = Modifier) {
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
            SectionLabel(text = stringResource(R.string.wallet_daily_fee))

            val fee = state.standing.dailyFee
            if (fee == null) {
                Text(
                    text = stringResource(R.string.wallet_fee_unavailable),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                return@Column
            }

            FeeRow(
                label = stringResource(R.string.wallet_fee_vehicle),
                value = stringResource(fee.vehicleType.labelRes()),
            )
            FeeRow(
                label = stringResource(R.string.wallet_fee_rate),
                value = stringResource(
                    R.string.wallet_fee_rate_value,
                    state.standing.dailyRate?.let { MoneyFormat.rupees(it) } ?: MoneyFormat.EMPTY,
                ),
            )

            // "PAID ✓ (1st trip free)" is two facts in one line, and the qualifier only belongs on
            // a day the free trip has not been spent — see `WalletFeeStanding.firstTripStillFree`.
            val status = stringResource(
                if (state.standing.feePaid) R.string.wallet_fee_paid else R.string.wallet_fee_unpaid,
            )
            FeeRow(
                label = stringResource(R.string.wallet_fee_today),
                value = if (state.standing.firstTripStillFree) {
                    stringResource(R.string.wallet_fee_status_first_free, status)
                } else {
                    status
                },
                accent = if (state.standing.feePaid) MageRideTheme.status.success else MageRideTheme.status.warning,
            )
        }
    }
}

/** The wireframe's `kv` — a muted label on the left, the value on the right. */
@Composable
private fun FeeRow(label: String, value: String, modifier: Modifier = Modifier, accent: Color? = null) {
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
            color = accent ?: MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * The two banner states, ranked hardest first.
 *
 * Overdrawn is D5' §9.4's *"Top Up Required"*, *"below one day's fee"* is D2' §SCR-DA-021's, and
 * the low-balance nudge is the softest of the three — see [WalletState.belowDayFee] on why the
 * first two are different questions rather than one restated.
 */
@Composable
private fun WalletNotice(state: WalletState, modifier: Modifier = Modifier) {
    when {
        state.alert is WalletAlert.TopUpRequired -> DashboardBanner(
            modifier = modifier,
            text = stringResource(
                R.string.wallet_top_up_required,
                MoneyFormat.rupees((state.alert as WalletAlert.TopUpRequired).owed),
            ),
            accent = MaterialTheme.colorScheme.error,
        )

        state.belowDayFee -> DashboardBanner(
            modifier = modifier,
            text = stringResource(
                R.string.wallet_below_day_fee,
                state.standing.dailyRate?.let { MoneyFormat.rupees(it) } ?: MoneyFormat.EMPTY,
            ),
            accent = MaterialTheme.colorScheme.error,
        )

        state.alert is WalletAlert.LowBalance -> DashboardBanner(
            modifier = modifier,
            text = stringResource(R.string.wallet_low_balance, MoneyFormat.rupees(state.threshold)),
            accent = MageRideTheme.status.warning,
        )

        else -> Unit
    }
}

/** The wireframe's two `btn-row`s — one CTA and three outlined actions. */
@Composable
private fun WalletActions(
    onTopUp: () -> Unit,
    onRequestCredit: () -> Unit,
    onTransferCredit: () -> Unit,
    onHistory: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        MageRideCta(label = stringResource(R.string.wallet_action_top_up), onClick = onTopUp)

        Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
            OutlinedButton(onClick = onRequestCredit, modifier = Modifier.weight(1f)) {
                Text(text = stringResource(R.string.wallet_action_request))
            }
            OutlinedButton(onClick = onTransferCredit, modifier = Modifier.weight(1f)) {
                Text(text = stringResource(R.string.wallet_action_transfer))
            }
        }
        OutlinedButton(onClick = onHistory, modifier = Modifier.fillMaxWidth()) {
            Text(text = stringResource(R.string.wallet_action_history))
        }
    }
}

/** *"Warn me below Rs 200"* — the driver's own low-balance line (US-9.9). */
@Composable
private fun ThresholdRow(state: WalletState, onEdit: () -> Unit, modifier: Modifier = Modifier) {
    TextButton(onClick = onEdit, modifier = modifier) {
        Text(
            text = stringResource(R.string.wallet_threshold_row, MoneyFormat.rupees(state.threshold)),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}

/**
 * The threshold dialog.
 *
 * The body says the setting lives on this handset, because it does and because a driver who
 * believed they had changed when MageRide texts them would be misled — the `LOW_BALANCE` push runs
 * on the admin's figure and there is no route that would let this one reach it.
 */
@Composable
private fun ThresholdDialog(current: Long, onSave: (Long) -> Unit, onReset: () -> Unit, onDismiss: () -> Unit) {
    var typed by remember { mutableStateOf(WalletInput.rupeesOf(current)) }
    val amountMinor = WalletInput.amountMinor(typed)

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = stringResource(R.string.wallet_threshold_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                Text(
                    text = stringResource(R.string.wallet_threshold_body),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                LabelledTextField(
                    label = stringResource(R.string.wallet_amount_label),
                    value = typed,
                    onValueChange = { typed = WalletInput.rupeeDigits(it) },
                    prefix = MoneyFormat.PREFIX,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { amountMinor?.let(onSave) }, enabled = amountMinor != null) {
                Text(text = stringResource(R.string.action_save))
            }
        },
        dismissButton = {
            TextButton(onClick = onReset) { Text(text = stringResource(R.string.wallet_threshold_reset)) }
        },
    )
}
