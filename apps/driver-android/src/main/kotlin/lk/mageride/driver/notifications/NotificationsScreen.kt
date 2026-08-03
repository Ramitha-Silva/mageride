package lk.mageride.driver.notifications

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-034 · alerts** (Epic 10, D5' §14.4).
 *
 * The wireframe: a `‹ Alerts` app bar over a list of rows, each a tinted leading square, a headline
 * and a relative time — *"Directional expiring in 10 min · 2 min ago"*.
 *
 * **Shimmer while loading**, which D2' names for this screen: three placeholder rows rather than a
 * spinner, because the list has a shape and a spinner over an empty column reads as an empty inbox.
 *
 * @param onOpen A row's deep link resolved to a screen. Unresolvable links are already `null` — see
 *   [NotificationsViewModel].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun NotificationsScreen(onBack: () -> Unit, onOpen: (DriverRoute) -> Unit, modifier: Modifier = Modifier) {
    val viewModel: NotificationsViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.alerts_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
                actions = {
                    if (state.alerts.any { !it.read }) {
                        TextButton(onClick = viewModel::markAllRead) {
                            Text(text = stringResource(R.string.alerts_mark_all_read))
                        }
                    }
                },
            )
        },
    ) { insets ->
        Column(modifier = Modifier.padding(insets).fillMaxSize()) {
            when {
                state.loading -> AlertShimmer()

                state.isEmpty -> Text(
                    text = stringResource(R.string.alerts_empty),
                    modifier = Modifier.padding(MageRideTheme.spacing.lg),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                else -> LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(MageRideTheme.spacing.md),
                    verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
                ) {
                    items(state.alerts, key = DriverAlert::id) { alert ->
                        AlertRow(alert = alert, onClick = { viewModel.open(alert) })
                    }
                }
            }
        }
    }

    LaunchedEffect(state.opening) {
        state.opening?.let {
            onOpen(it)
            viewModel.consumeOpening()
        }
    }
}

/** One `.listrow` — the tinted square, the headline and the relative time. */
@OptIn(ExperimentalTime::class)
@Composable
private fun AlertRow(alert: DriverAlert, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val kind = AlertKind.of(alert.type)

    Row(
        modifier = modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Box(
            modifier = Modifier
                .size(ControlTokens.ListRowIcon)
                .background(
                    color = alertTone(kind).copy(alpha = SQUARE_TINT),
                    shape = RoundedCornerShape(MageRideTheme.radius.sm),
                ),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = kind.icon,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.ChipIcon),
                tint = alertTone(kind),
            )
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = alert.title ?: stringResource(kind.label),
                style = MaterialTheme.typography.bodyMedium,
                // An unread alert is the one thing on this list a driver is scanning for.
                fontWeight = if (alert.read) FontWeight.Normal else FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurface,
            )
            alert.body?.takeIf(String::isNotBlank)?.let { body ->
                Text(
                    text = body,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = alertAge(alert.receivedAt),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.outlineVariant,
            )
        }
    }
}

/** D2' §SCR-DA-034's *"shimmer loading"* — three rows of the shape that is coming. */
@Composable
private fun AlertShimmer(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        repeat(SHIMMER_ROWS) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                Box(
                    modifier = Modifier
                        .size(ControlTokens.ListRowIcon)
                        .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            RoundedCornerShape(MageRideTheme.radius.sm),
                        ),
                )
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .height(ControlTokens.ListRowIcon)
                        .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            RoundedCornerShape(MageRideTheme.radius.sm),
                        ),
                )
            }
        }
    }
}

/** How much of the tone tints a row's leading square. Light enough to read the icon over. */
private const val SQUARE_TINT = 0.18f

/** The wireframe draws four rows; three is enough to say "a list is coming". */
private const val SHIMMER_ROWS = 3
