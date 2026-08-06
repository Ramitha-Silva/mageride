package lk.mageride.passenger.ride

import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarBorder
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-019 — *"How was your ride with K. Fernando?"*.
 *
 * Stars, the four compliment chips, an optional comment, Submit (US-18.1).
 *
 * **Submit saves rather than sends, and the copy says so**, because there is no contract to send
 * it to: `ride.yaml` declares no rating operation and trip-state-svc's is scoped to a *session*,
 * which a Mode C ride is not. See [RateDriverViewModel] for the whole argument and the C080
 * handoff for the route this needs. Telling a passenger their rating was submitted would be
 * telling them something that did not happen.
 */
@Composable
internal fun RateDriverScreen(onDone: () -> Unit, model: RateDriverViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.queued) { if (state.queued) onDone() }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = state.driverName
                ?.let { stringResource(R.string.rate_title, it) }
                ?: stringResource(R.string.rate_title_unknown),
            style = MaterialTheme.typography.titleLarge,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )

        Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
            for (star in 1..RateDriverState.MAX_STARS) {
                Icon(
                    imageVector = if (star <= state.stars) Icons.Filled.Star else Icons.Outlined.StarBorder,
                    contentDescription = stringResource(R.string.rate_stars, star),
                    modifier = Modifier
                        .size(ControlTokens.Star)
                        .clickable { model.setStars(star) },
                    tint = if (star <= state.stars) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.outline
                    },
                )
            }
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            RatingTag.entries.forEach { tag ->
                FilterChip(
                    selected = tag in state.tags,
                    onClick = { model.toggleTag(tag) },
                    label = { Text(stringResource(tag.label)) },
                )
            }
        }

        LabelledTextField(
            label = stringResource(R.string.rate_comment),
            value = state.comment,
            onValueChange = model::onCommentChanged,
            placeholder = stringResource(R.string.rate_comment_hint),
            keyboardType = KeyboardType.Text,
            imeAction = ImeAction.Done,
        )

        state.error?.let { InlineError(message = stringResource(it)) }

        MageRideCta(
            label = stringResource(R.string.rate_submit),
            onClick = model::submit,
            enabled = state.canSubmit,
            loading = state.submitting,
        )
    }
}
