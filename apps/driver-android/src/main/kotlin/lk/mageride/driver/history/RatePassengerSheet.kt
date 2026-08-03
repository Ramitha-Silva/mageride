package lk.mageride.driver.history

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.driver.R
import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * **The rate-passenger sheet** — AL-35's ruling, drawn.
 *
 * > *"Tapping 'Rate ★' on a completed trip opens a **modal bottom sheet** to rate the passenger 1–5
 * > stars (+ optional comment) (US-18.2) — it is **no longer an inline card**."*
 *
 * The wireframe's contents, top to bottom: the grab handle, *"Rate passenger"*, the passenger's
 * avatar with their name and the trip under it, the five stars, the optional comment field and the
 * `Submit rating` CTA.
 *
 * **One completed ride can never be rated: a proxy booking whose rider is unregistered** (P-01).
 * There is no `iam.users` row to be the `ratee_id`, and `DriverRatingInput.passengerId` is required
 * — so the sheet says so and the CTA stays dead rather than posting a rating about nobody.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RatePassengerSheet(
    state: RatePassengerState?,
    viewModel: RideHistoryViewModel,
    modifier: Modifier = Modifier,
) {
    if (state == null) return
    val locale = LocalConfiguration.current.locales[0]

    ModalBottomSheet(
        onDismissRequest = viewModel::dismissRating,
        modifier = modifier,
        sheetState = rememberModalBottomSheetState(),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(
                    start = MageRideTheme.spacing.md,
                    end = MageRideTheme.spacing.md,
                    bottom = MageRideTheme.spacing.lg,
                ),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Text(
                text = stringResource(R.string.history_rate_title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )

            PassengerRow(
                name = state.passengerName ?: stringResource(R.string.history_passenger_unnamed),
                trip = "${tripRoute(state.trip.summary)} ${Symbols.DOT} " +
                    ScheduleLabels.date(state.trip.summary.startedAt, locale),
            )

            StarRow(stars = state.stars, onStarsChange = viewModel::onStarsChange)

            LabelledTextField(
                label = stringResource(R.string.history_rate_comment_label),
                value = state.comment,
                onValueChange = viewModel::onCommentChange,
                placeholder = stringResource(R.string.history_rate_comment_hint),
                singleLine = false,
            )

            if (!state.loading && state.passengerId == null) {
                Text(
                    text = stringResource(R.string.history_rate_no_passenger),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            MageRideCta(
                label = stringResource(R.string.history_rate_submit),
                onClick = viewModel::submitRating,
                enabled = state.canSubmit,
                loading = state.submitting,
            )
        }
    }
}

/** The wireframe's avatar and the two lines beside it. */
@Composable
private fun PassengerRow(name: String, trip: String, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Surface(
            modifier = Modifier.size(ControlTokens.AvatarSmall),
            shape = CircleShape,
            color = MaterialTheme.colorScheme.primaryContainer,
        ) {
            Box(contentAlignment = Alignment.Center) {
                Icon(
                    imageVector = Icons.Outlined.Person,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
        }
        Column {
            Text(
                text = name,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = trip,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/**
 * The five stars, as five tap targets.
 *
 * `role = Role.RadioButton` on each: one of five is exactly what a star rating is, and TalkBack
 * announcing five buttons would leave a driver with no way to know which was chosen.
 */
@Composable
private fun StarRow(stars: Int, onStarsChange: (Int) -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs, Alignment.CenterHorizontally),
    ) {
        for (star in RatePassengerState.MIN_STARS..RatePassengerState.MAX_STARS) {
            Text(
                text = if (star <= stars) Symbols.STAR_FILLED else Symbols.STAR_EMPTY,
                modifier = Modifier
                    .clickable(
                        role = Role.RadioButton,
                        onClickLabel = star.toString(),
                        onClick = { onStarsChange(star) },
                    )
                    .padding(MageRideTheme.spacing.xxs),
                style = MaterialTheme.typography.headlineMedium,
                color = MageRideTheme.status.warning,
                textAlign = TextAlign.Center,
            )
        }
    }
}
