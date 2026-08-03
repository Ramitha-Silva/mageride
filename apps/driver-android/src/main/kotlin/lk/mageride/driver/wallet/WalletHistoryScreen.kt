package lk.mageride.driver.wallet

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.DateRange
import androidx.compose.material.icons.outlined.FileDownload
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DateRangePicker
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import androidx.compose.material3.rememberDateRangePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.util.BusinessCalendar
import org.koin.androidx.compose.koinViewModel
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * **SCR-DA-025 · payment & fee history** (US-9A.6, US-9A.19, D-13).
 *
 * The wireframe, top to bottom: a `‹ History` app bar with a filter action, the four chips, and the
 * ledger rows — a tinted `−`/`＋` badge, the line's name and Colombo date, and the signed amount.
 *
 * **The app bar carries two actions where the wireframe draws one.** Its 🔍 is D2' §SCR-DA-025's
 * *"date-range filter"* and is the calendar here; the download beside it is the same section's
 * *"Statement download (US-9A.19)"*, which the sketch's icon row simply has no room for. See
 * [WalletHistoryViewModel] on why a statement is also the whole of "receipt download".
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun WalletHistoryScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: WalletHistoryViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    var pickingRange by remember { mutableStateOf(false) }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.wallet_history_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
                actions = {
                    IconButton(onClick = { pickingRange = true }) {
                        Icon(
                            imageVector = Icons.Outlined.DateRange,
                            contentDescription = stringResource(R.string.wallet_history_range),
                            tint = if (state.hasRange) {
                                MaterialTheme.colorScheme.primary
                            } else {
                                MaterialTheme.colorScheme.onSurfaceVariant
                            },
                        )
                    }
                    DownloadMenu(busy = state.exporting != null, onExport = viewModel::export)
                },
            )
        },
    ) { insets ->
        Column(modifier = Modifier.padding(insets).fillMaxSize()) {
            if (state.loading || state.exporting != null) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            }
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            state.exported?.let { format ->
                DashboardBanner(
                    text = stringResource(R.string.wallet_statement_ready, format.extension.uppercase()),
                    accent = MageRideTheme.status.success,
                )
            }

            FilterChips(selected = state.filter, onSelect = viewModel::selectFilter)

            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
            ) {
                if (state.isEmpty) {
                    item {
                        Text(
                            text = stringResource(R.string.wallet_history_empty),
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            textAlign = TextAlign.Center,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(MageRideTheme.spacing.lg),
                        )
                    }
                } else {
                    items(items = state.visible, key = { it.entryId }) { line -> LedgerRow(line = line) }
                }
            }
        }
    }

    if (pickingRange) {
        RangeDialog(
            onConfirm = { from, to ->
                viewModel.setRange(from, to)
                pickingRange = false
            },
            onDismiss = { pickingRange = false },
        )
    }
}

/** The wireframe's `All · Fees · Top-ups · Transfers`. Local — switching one re-hits nothing. */
@Composable
private fun FilterChips(selected: HistoryFilter, onSelect: (HistoryFilter) -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = MageRideTheme.spacing.md, vertical = MageRideTheme.spacing.xxs),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        HistoryFilter.entries.forEach { filter ->
            FilterChip(
                selected = filter == selected,
                onClick = { onSelect(filter) },
                label = { Text(text = stringResource(filter.label)) },
            )
        }
    }
}

/**
 * One ledger line.
 *
 * `amountMinor` is **signed on the wire** — one of the ledger columns D3' §0 exempts from the
 * non-negative rule — so the sign, the badge and the colour all read off the same number rather
 * than off the kind. That is what keeps an `adjustment` (which can go either way) honest.
 */
@OptIn(ExperimentalTime::class)
@Composable
private fun LedgerRow(line: WalletTransaction, modifier: Modifier = Modifier) {
    val debit = line.isDebit
    val accent = if (debit) MaterialTheme.colorScheme.error else MageRideTheme.status.success
    val locale = LocalConfiguration.current.locales[0]

    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        SignBadge(debit = debit, accent = accent)

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(LedgerKinds.labelFor(line.kind)),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = ScheduleLabels.date(line.occurredAt, locale),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        Column(horizontalAlignment = Alignment.End) {
            Text(
                text = MoneyFormat.rupees(line.amountMinor),
                style = MaterialTheme.typography.titleMedium,
                color = accent,
            )
            Text(
                text = MoneyFormat.rupees(line.balanceAfterMinor),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** The wireframe's tinted `−` / `＋` disc. The glyphs are symbols, so they are not translated. */
@Composable
private fun SignBadge(debit: Boolean, accent: Color, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(ControlTokens.RowIcon)
            .background(accent.copy(alpha = BADGE_TINT), CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = if (debit) DEBIT_GLYPH else CREDIT_GLYPH,
            style = MaterialTheme.typography.labelSmall,
            color = accent,
        )
    }
}

/** US-9A.19's download, in the two formats `wallet.yaml` declares. */
@Composable
private fun DownloadMenu(busy: Boolean, onExport: (StatementFormat) -> Unit, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }

    Box(modifier = modifier) {
        IconButton(onClick = { open = true }, enabled = !busy) {
            Icon(
                imageVector = Icons.Outlined.FileDownload,
                contentDescription = stringResource(R.string.wallet_statement_download),
            )
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            StatementFormat.entries.forEach { format ->
                DropdownMenuItem(
                    text = { Text(text = format.extension.uppercase()) },
                    onClick = {
                        open = false
                        onExport(format)
                    },
                )
            }
        }
    }
}

/**
 * D2' §SCR-DA-025's date-range filter.
 *
 * The picker answers UTC-midnight millis for whole calendar days, and those are turned into
 * **Asia/Colombo** business dates (D-13, D-38) — which is what wallet-svc filters on. Reading them
 * in the handset's zone would move the boundary for a driver whose phone came back from abroad
 * still on its old one.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalTime::class)
@Composable
private fun RangeDialog(
    onConfirm: (lk.mageride.shared.data.models.BusinessDate?, lk.mageride.shared.data.models.BusinessDate?) -> Unit,
    onDismiss: () -> Unit,
) {
    val picker = rememberDateRangePickerState()

    DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(
                onClick = {
                    onConfirm(
                        picker.selectedStartDateMillis?.let(::businessDate),
                        picker.selectedEndDateMillis?.let(::businessDate),
                    )
                },
            ) {
                Text(text = stringResource(R.string.action_save))
            }
        },
        dismissButton = {
            // Clearing the range is the way back to "everything the server sends"; a driver who
            // opened the calendar by accident needs a door out that is not a range.
            TextButton(onClick = { onConfirm(null, null) }) {
                Text(text = stringResource(R.string.wallet_history_range_clear))
            }
        },
    ) {
        DateRangePicker(state = picker, modifier = Modifier.fillMaxWidth())
    }
}

@OptIn(ExperimentalTime::class)
private fun businessDate(epochMillis: Long) = BusinessCalendar.businessDate(Instant.fromEpochMilliseconds(epochMillis))

/** How much of the accent a sign badge keeps. Light enough to read the glyph over. */
private const val BADGE_TINT = 0.18f

/** Symbols, not copy — the same rule `Rs` and `→` follow. */
private const val DEBIT_GLYPH = "−"
private const val CREDIT_GLYPH = "+"
