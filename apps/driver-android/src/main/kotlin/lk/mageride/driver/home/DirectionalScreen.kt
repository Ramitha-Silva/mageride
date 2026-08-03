package lk.mageride.driver.home

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Place
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-013 · Directional Travel** (US-6A.17–23, DT-01..DT-08).
 *
 * The wireframe, top to bottom: the destination field (search, or ★ Home), the map preview with the
 * ➤ heading marker, the *"Uses left today"* / *"Max duration"* pair, **Set Direction (uses 1)**, and
 * — when a filter is running — the persistent card with the destination, what is left of it,
 * **Turn Off** and the note that only direction-matching hires reach the driver.
 *
 * **Turning it off still costs the use** (US-6A.19). The card says so, because a driver who thinks
 * they are getting the activation back will spend both before lunch.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DirectionalScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: DirectionalViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.directional_title)) },
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
        ) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }

            DestinationField(state = state, onSearch = viewModel::search, onChoose = viewModel::choose)

            // The wireframe's small map with the ➤ heading marker. The arrow is rotated to the
            // bearing from the driver to the destination, not to the vehicle's own course — the
            // point of the preview is "this is the way you said you were going".
            DriverHomeMap(
                position = state.position,
                vehicleType = null,
                heading = state.headingDeg,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(ControlTokens.MapPreview)
                    .clip(RoundedCornerShape(MageRideTheme.radius.md)),
            )

            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                MetricCard(
                    label = stringResource(R.string.directional_uses_left),
                    value = state.usesRemaining.toString(),
                    modifier = Modifier.weight(1f),
                )
                MetricCard(
                    label = stringResource(R.string.directional_max_duration),
                    value = state.maxDuration
                        ?.let { MoneyFormat.countdown(it.inWholeSeconds) }
                        ?: DirectionalLabels.UNKNOWN,
                    modifier = Modifier.weight(1f),
                )
            }

            MageRideCta(
                label = stringResource(R.string.directional_set),
                onClick = viewModel::setDirection,
                enabled = state.canSet,
                loading = state.busy,
            )

            if (state.isActive) {
                SectionLabel(text = stringResource(R.string.directional_when_active))
                ActiveFilterCard(state = state, onTurnOff = viewModel::turnOff)
            }
        }
    }
}

/**
 * The destination field and its shortlist — a geocoder search over ★ Home and ★ Work.
 *
 * The saved addresses come first and stay first: the wireframe pins ★ Home to the top because a
 * driver setting a direction at the end of a shift is almost always heading there, and typing an
 * address one-handed at a junction is the thing this shortcut exists to avoid.
 */
@Composable
private fun DestinationField(
    state: DirectionalState,
    onSearch: (String) -> Unit,
    onChoose: (DirectionalDestination) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
        OutlinedTextField(
            value = state.query,
            onValueChange = onSearch,
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            label = { Text(text = stringResource(R.string.directional_destination)) },
            placeholder = { Text(text = stringResource(R.string.directional_search_hint)) },
        )

        state.suggestions.forEach { suggestion ->
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { onChoose(suggestion) }
                    .padding(MageRideTheme.spacing.xs),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Icon(
                    imageVector = if (suggestion.isHome) Icons.Outlined.Home else Icons.Outlined.Place,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
                Text(
                    text = suggestion.label,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }
        }
    }
}

/** One of the wireframe's two centred `card fill` metrics. */
@Composable
private fun MetricCard(label: String, value: String, modifier: Modifier = Modifier) {
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
 * The persistent active banner (US-6A.21, DT-08).
 *
 * *"⮕ To Nugegoda · 1:42 left"*, **Turn Off**, and the two facts that stop the filter being a
 * mystery: how many activations are left, and that only direction-matching hires will reach the
 * driver while it runs. The card pulses in the last ten minutes, which is the same threshold
 * notify-svc's `directional.expiring` push uses.
 */
@Composable
private fun ActiveFilterCard(state: DirectionalState, onTurnOff: () -> Unit, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(
            containerColor = if (state.isExpiringSoon) {
                MageRideTheme.status.warning.copy(alpha = CARD_TINT)
            } else {
                MaterialTheme.colorScheme.primaryContainer
            },
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = stringResource(
                        R.string.directional_active_to,
                        state.filter?.label.orEmpty(),
                        MoneyFormat.countdown(state.timeRemaining.inWholeSeconds),
                    ),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onTurnOff, enabled = !state.busy) {
                    Text(text = stringResource(R.string.directional_turn_off))
                }
            }
            Text(
                text = stringResource(R.string.directional_uses_remaining, state.usesRemaining),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                text = stringResource(R.string.directional_active_note),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/**
 * The one value on this screen that is a symbol rather than a sentence.
 *
 * An em dash means "we have not been told" in all three languages; three identical entries in the
 * three `strings.xml` files is what `StringResourceTest` fails on (C068's rule).
 */
private object DirectionalLabels {
    const val UNKNOWN: String = "—"
}

/** How much of the warning colour the pulsing card keeps. */
private const val CARD_TINT = 0.22f
