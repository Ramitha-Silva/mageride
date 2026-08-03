package lk.mageride.driver.level

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.WarningAmber
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.home.DashboardLabels
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.domain.dispatch.DriverLevelRules
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-019 · Driver Level & stats** (US-6A.6, US-6A.14).
 *
 * The wireframe, top to bottom: a `‹` app bar, the big `L3` badge in a `primaryContainer` square,
 * the points line, the progress bar, the **Acceptance** / **No-shows** pair, and the warning that
 * three passenger reports cost a level and a temporary delisting.
 *
 * Read [DriverLevelViewModel] before changing the points line or the warning — both differ from the
 * wireframe's sample text for reasons that are recorded, not accidental.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DriverLevelScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: DriverLevelViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.level_title)) },
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
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }

            LevelBadge(level = state.level)
            PointsLine(state = state)
            LevelProgress(progress = state.progress)
            StatRow(state = state)
            DelistingWarning()
        }
    }
}

/** The 96 dp `primaryContainer` square with `L3` in it. `—` while reputation has not answered. */
@Composable
private fun LevelBadge(level: Int?, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .size(ControlTokens.LevelBadge)
            .clip(RoundedCornerShape(MageRideTheme.radius.card))
            .background(MaterialTheme.colorScheme.primaryContainer),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = level?.let(DashboardLabels::level) ?: LevelLabels.UNKNOWN,
            style = MaterialTheme.typography.displaySmall,
            color = MaterialTheme.colorScheme.primary,
        )
    }
}

/**
 * *"310 / 500 pts → Level 3"*, or *"Level 3 is the top level"* at the cap.
 *
 * D5' §4.2 stops at three; the wireframe's *"→ Level 4"* does not exist. See [DriverLevelViewModel].
 */
@Composable
private fun PointsLine(state: DriverLevelState, modifier: Modifier = Modifier) {
    val next = state.nextLevel

    Text(
        text = when {
            state.level == null -> stringResource(R.string.level_loading)
            next == null -> stringResource(R.string.level_top, DriverLevelRules.MAX_LEVEL)
            else -> stringResource(R.string.level_points_to_next, state.points, state.threshold, next)
        },
        style = MaterialTheme.typography.titleMedium,
        color = MaterialTheme.colorScheme.onSurface,
        textAlign = TextAlign.Center,
        modifier = modifier,
    )
}

/** The wireframe's 8 px bar. D2' §SCR-DA-019's animation is *"progress fill"*, so it animates. */
@Composable
private fun LevelProgress(progress: Float, modifier: Modifier = Modifier) {
    val filled by animateFloatAsState(targetValue = progress, label = "level-progress")

    LinearProgressIndicator(
        progress = { filled },
        modifier = modifier
            .fillMaxWidth()
            .height(ControlTokens.LevelProgress)
            .clip(RoundedCornerShape(MageRideTheme.radius.sm)),
        color = MaterialTheme.colorScheme.primary,
        trackColor = MaterialTheme.colorScheme.surfaceVariant,
        gapSize = ControlTokens.ProgressGap,
        drawStopIndicator = {},
    )
}

/** The **Acceptance** / **No-shows** pair (US-6A.14, US-6A.7). */
@Composable
private fun StatRow(state: DriverLevelState, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        StatCard(
            label = stringResource(R.string.level_acceptance),
            value = state.acceptancePercent?.let(LevelLabels::percent) ?: LevelLabels.UNKNOWN,
            modifier = Modifier.weight(1f),
        )
        StatCard(
            label = stringResource(R.string.level_no_shows),
            value = state.noShows?.toString() ?: LevelLabels.UNKNOWN,
            modifier = Modifier.weight(1f),
        )
    }
}

/** One of the wireframe's two centred `card fill` metrics. */
@Composable
private fun StatCard(label: String, value: String, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.sm),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                text = value,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
    }
}

/**
 * D5' §4.2's level-down rule, said out loud.
 *
 * **Always shown, because it can only be the rule.** No app-facing read carries the report count —
 * see [DriverLevelViewModel] — so this cannot become "you are on your second report", and a banner
 * that appeared only sometimes would imply it had.
 */
@Composable
private fun DelistingWarning(modifier: Modifier = Modifier) {
    DashboardBanner(
        text = stringResource(R.string.level_reports_warning, DriverLevelRules.REPORTS_PER_LEVEL_DOWN),
        accent = MageRideTheme.status.warning,
        icon = Icons.Outlined.WarningAmber,
        modifier = modifier,
    )
}

/**
 * The two values on this screen that are symbols rather than sentences.
 *
 * An em dash means "we have not been told" and `92%` is a number with a sign after it, in all three
 * languages alike; three identical entries in the three `strings.xml` files is exactly what
 * `StringResourceTest` fails on (C068's rule, and the same one `Rs` and `+94` follow).
 */
private object LevelLabels {

    const val UNKNOWN: String = "—"

    /** `92` → `92%` — US-6A.14's acceptance rate. */
    fun percent(value: Int): String = "$value%"
}
