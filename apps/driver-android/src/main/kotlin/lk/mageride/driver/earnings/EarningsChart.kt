package lk.mageride.driver.earnings

import androidx.annotation.StringRes
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.query.EarningsPeriod

/**
 * The wireframe's `tabbar2` — **Today · Week · Month** (D2' §SCR-DA-020's `TabRow`).
 *
 * The three periods are `EarningsPeriod`'s own entries rather than a list this screen keeps, so a
 * fourth window added to `query.yaml` appears here by compiling.
 */
@Composable
internal fun EarningsPeriodTabs(
    selected: EarningsPeriod,
    onSelect: (EarningsPeriod) -> Unit,
    modifier: Modifier = Modifier,
) {
    TabRow(
        selectedTabIndex = EarningsPeriod.entries.indexOf(selected),
        modifier = modifier,
        containerColor = MaterialTheme.colorScheme.background,
        contentColor = MaterialTheme.colorScheme.primary,
    ) {
        EarningsPeriod.entries.forEach { period ->
            Tab(
                selected = period == selected,
                onClick = { onSelect(period) },
                text = { Text(text = stringResource(period.labelRes()), style = MaterialTheme.typography.labelLarge) },
            )
        }
    }
}

/** The trilingual name of a period tab. */
@StringRes
internal fun EarningsPeriod.labelRes(): Int = when (this) {
    EarningsPeriod.TODAY -> R.string.earnings_period_today
    EarningsPeriod.WEEK -> R.string.earnings_period_week
    EarningsPeriod.MONTH -> R.string.earnings_period_month
}

/**
 * The wireframe's `.bars` — the earnings trend, drawn as bars rather than as a line.
 *
 * Compose primitives rather than a charting library: this is a bar per bucket with one highlighted,
 * and a dependency that draws axes, legends and tooltips would be a large addition for a control
 * whose whole specification is *"height ∝ value, highlight the current one"*. D2' §SCR-DA-020 names
 * `Canvas`/Vico as alternatives and does not require either.
 *
 * **Heights are relative to the biggest bucket, not to an absolute rupee scale.** A driver reading
 * this wants to see which hour or day was the good one; a fixed scale would flatten a quiet week
 * into nothing. The bars grow into place, which is §SCR-DA-020's *"chart draw"* animation.
 */
@Composable
internal fun EarningsChart(buckets: List<EarningsBucket>, modifier: Modifier = Modifier) {
    if (buckets.isEmpty()) return

    val peak = buckets.maxOf { it.netMinor }.coerceAtLeast(1L)

    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(ControlTokens.EarningsChart),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.Bottom,
    ) {
        buckets.forEach { bucket ->
            ChartBar(bucket = bucket, peak = peak, modifier = Modifier.weight(1f))
        }
    }
}

/** One bar and its label. */
@Composable
private fun ChartBar(bucket: EarningsBucket, peak: Long, modifier: Modifier = Modifier) {
    val fraction by animateFloatAsState(
        // Every bucket keeps a sliver so an empty hour is visible as an empty hour rather than as a
        // gap in the axis — a driver counting bars has to be able to count the quiet ones.
        targetValue = (bucket.netMinor.toFloat() / peak).coerceIn(MIN_BAR, 1f),
        label = "earnings-bar",
    )

    Column(
        modifier = modifier.fillMaxHeight(),
        verticalArrangement = Arrangement.Bottom,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
            contentAlignment = Alignment.BottomCenter,
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .fillMaxHeight(fraction)
                    .background(
                        color = if (bucket.current) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.primaryContainer
                        },
                        shape = RoundedCornerShape(
                            topStart = MageRideTheme.radius.sm,
                            topEnd = MageRideTheme.radius.sm,
                        ),
                    ),
            )
        }
        Text(
            text = bucket.label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** The shortest a bar gets, so an empty bucket still occupies its place on the axis. */
private const val MIN_BAR = 0.04f
