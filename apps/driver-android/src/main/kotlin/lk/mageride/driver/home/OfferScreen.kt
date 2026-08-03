package lk.mageride.driver.home

import android.media.RingtoneManager
import android.os.VibrationEffect
import android.os.Vibrator
import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.getSystemService
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.PackageLabels
import lk.mageride.driver.ui.component.CountdownRing
import lk.mageride.driver.ui.component.SolidBadge
import lk.mageride.driver.ui.theme.CtaTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.domain.dispatch.OfferOutcome
import lk.mageride.shared.domain.dispatch.RideOffer

/**
 * **SCR-DA-014 · the incoming dispatch takeover** (US-6A.2/6A.3, R-02, E-01).
 *
 * A `Dialog` sized to the whole window rather than a route: the offer belongs to the dashboard,
 * fifteen seconds is not long enough to navigate, and back must not dismiss it — a driver who
 * swipes out of an offer they meant to accept has lost it. The two ways out are the two buttons and
 * the clock.
 *
 * @param showFeeNote US-9.1's *"2nd trip today — the daily fee deducts on accept"*. Passed in from
 *   the dashboard's own `GET /v1/fees/{driverId}/today` rather than read again here: it is the same
 *   fact, and a second read inside the fifteen seconds would compete with the enrichment one.
 * @param directional DT-08's badge. The `offer.created` envelope does not carry a
 *   `directionalMatched` flag, so it is derived from the driver's **own** filter being active —
 *   which is sound, because dispatch only offers direction-matching rides while one is (DT-01).
 * @param onFinished Called once with the won ride's id, or `null` for every other ending.
 */
@Composable
internal fun OfferTakeover(
    state: OfferUiState,
    showFeeNote: Boolean,
    feeAmount: String,
    directional: Boolean,
    onAccept: () -> Unit,
    onReject: () -> Unit,
    onFinished: (Ulid?) -> Unit,
) {
    val outcome = state.outcome
    val terminal = outcome != null && outcome !is OfferOutcome.Won

    // Won navigates immediately; an expiry or a decline auto-dismisses, which is D2's own
    // "expired → auto-dismiss". Only the endings that need explaining stay on screen.
    LaunchedEffect(outcome) {
        when (outcome) {
            is OfferOutcome.Won -> onFinished(outcome.ride.rideId)
            OfferOutcome.Expired, OfferOutcome.Declined -> onFinished(null)
            else -> Unit
        }
    }

    if (!state.isLive && !terminal) return

    Dialog(
        onDismissRequest = {},
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            dismissOnBackPress = false,
            dismissOnClickOutside = false,
        ),
    ) {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.inverseSurface) {
            val message = outcome?.let(::outcomeMessage)
            if (message != null) {
                OfferOutcomeCard(message = message, onDismiss = { onFinished(null) })
            } else {
                OfferContent(
                    state = state,
                    showFeeNote = showFeeNote,
                    feeAmount = feeAmount,
                    directional = directional,
                    onAccept = onAccept,
                    onReject = onReject,
                )
            }
        }
    }

    OfferAlert(offerId = state.offer?.offerId)
}

/** The wireframe's dark takeover: ring, badges, fare, the two places, the fee note, two buttons. */
@Composable
private fun OfferContent(
    state: OfferUiState,
    showFeeNote: Boolean,
    feeAmount: String,
    directional: Boolean,
    onAccept: () -> Unit,
    onReject: () -> Unit,
) {
    val offer = state.offer ?: return
    val detail = state.detail
    val fareMinor = detail?.fare?.amountMinor ?: offer.fareEstimateMinor

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        // The ring is scaled against the fixed fifteen-second TTL rather than against whatever is
        // left, which is what makes a push that took two seconds to arrive show thirteen seconds of
        // ring instead of a full one — `RideOffer.progress` derives the same figure from the
        // deadline, and this is that figure once the countdown owns the clock.
        CountdownRing(
            progress = (state.remaining / RideOffer.TTL).coerceIn(0.0, 1.0).toFloat(),
            seconds = state.remaining.inWholeSeconds.toInt(),
            urgent = state.isUrgent,
        )

        OfferBadges(state = state, directional = directional)

        Text(
            text = MoneyFormat.rupees(fareMinor),
            style = MaterialTheme.typography.displaySmall,
            color = MaterialTheme.colorScheme.inverseOnSurface,
        )
        Text(
            text = stringResource(PackageLabels.payment(detail?.paymentMethod)),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.inverseOnSurface,
        )

        PlaceRow(
            label = stringResource(R.string.offer_pickup),
            value = detail?.pickup?.address.orEmpty(),
        )
        PlaceRow(
            label = stringResource(R.string.offer_drop),
            value = detail?.dropoff?.address.orEmpty(),
        )

        if (showFeeNote) {
            Text(
                text = stringResource(R.string.offer_fee_note, feeAmount),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.primary,
                textAlign = TextAlign.Center,
            )
        }

        Spacer(modifier = Modifier.weight(1f))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            OutlinedButton(
                onClick = onReject,
                modifier = Modifier.weight(1f).height(CtaTokens.Height),
                enabled = !state.deciding,
                shape = RoundedCornerShape(CtaTokens.Radius),
            ) {
                Text(text = stringResource(R.string.offer_reject))
            }
            Button(
                onClick = onAccept,
                modifier = Modifier.weight(1f).height(CtaTokens.Height),
                enabled = !state.deciding,
                shape = RoundedCornerShape(CtaTokens.Radius),
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.primary),
            ) {
                Text(text = stringResource(R.string.offer_accept), style = MaterialTheme.typography.titleMedium)
            }
        }
    }
}

/**
 * The three badges D2' pins above the fare.
 *
 * **Third-party booking** is P-05 — the booker is not the rider, which changes who the driver calls
 * and who is standing at the pickup. **Package · S/M/L** is US-20.3, and it is shown even when the
 * vehicle is compatible, because P-11 filters candidates but never overrides a driver's own
 * judgement about what will fit. **Directional** is DT-08's *"the filter is working"* signal.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun OfferBadges(state: OfferUiState, directional: Boolean, modifier: Modifier = Modifier) {
    val detail = state.detail
    FlowRow(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs, Alignment.CenterHorizontally),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        if (detail?.kind == RideKind.PROXY || state.offer?.isProxy == true) {
            SolidBadge(
                label = stringResource(R.string.offer_badge_proxy),
                accent = MaterialTheme.colorScheme.secondary,
            )
        }
        (detail?.packageSize ?: state.offer?.packageSize)?.let { size ->
            SolidBadge(
                label = stringResource(R.string.offer_badge_package, stringResource(PackageLabels.size(size))),
                accent = MageRideTheme.vehicle.truck,
            )
        }
        if (directional || state.offer?.directionalMatched == true) {
            SolidBadge(
                label = stringResource(R.string.offer_badge_directional),
                accent = MaterialTheme.colorScheme.primary,
            )
        }
    }
}

/** One of the wireframe's two `kv` rows — `● Pickup` and `◆ Drop`. */
@Composable
private fun PlaceRow(label: String, value: String, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(
                MaterialTheme.colorScheme.surfaceVariant.copy(alpha = CARD_TINT),
                RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(MageRideTheme.spacing.sm),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.inverseOnSurface,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.inverseOnSurface,
            modifier = Modifier.weight(1f),
            textAlign = TextAlign.End,
        )
    }
}

/** The endings that need a word before the driver is put back on the standby map. */
@Composable
private fun OfferOutcomeCard(@StringRes message: Int, onDismiss: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize().padding(MageRideTheme.spacing.lg), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
        ) {
            Text(
                text = stringResource(message),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.inverseOnSurface,
                textAlign = TextAlign.Center,
            )
            Button(onClick = onDismiss, shape = RoundedCornerShape(CtaTokens.Radius)) {
                Text(text = stringResource(R.string.action_dismiss))
            }
        }
    }
}

/**
 * D2' §SCR-DA-014's *"strong haptic on arrival"* and the ride-assigned tone.
 *
 * Keyed on the offer id so a recomposition does not buzz again, and so a **second** offer does.
 * Both are best-effort: a handset with no vibrator, or a driver in a silent profile, still gets the
 * screen — which is the part that matters.
 */
@Composable
private fun OfferAlert(offerId: String?) {
    val context = LocalContext.current

    LaunchedEffect(offerId) {
        if (offerId == null) return@LaunchedEffect

        @Suppress("DEPRECATION") // `VibratorManager` is API 31; this module's floor is 26.
        val vibrator = context.getSystemService<Vibrator>()
        runCatching { vibrator?.vibrate(VibrationEffect.createWaveform(HAPTIC_PATTERN, -1)) }

        runCatching {
            val uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_NOTIFICATION)
            RingtoneManager.getRingtone(context, uri)?.play()
        }
    }
}

/**
 * Why the takeover is closing, when the reason is not simply "time ran out".
 *
 * `Taken` and `Expired` are never collapsed — one says somebody was faster and the other says
 * nobody was, and a driver app that showed "too slow" for a ride nobody took would be lying about
 * their own acceptance rate.
 */
@StringRes
private fun outcomeMessage(outcome: OfferOutcome): Int? = when (outcome) {
    OfferOutcome.Taken -> R.string.offer_taken
    OfferOutcome.WalletBlocked -> R.string.offer_wallet_blocked
    is OfferOutcome.Failed -> R.string.error_generic
    else -> null
}

/** No delay before the first buzz — the offer is already two of its fifteen seconds old. */
private const val HAPTIC_DELAY_MS = 0L

/** Long enough to feel through a jacket pocket at a junction, short enough not to be a drone. */
private const val HAPTIC_BUZZ_MS = 400L

/** The gap that makes it two buzzes rather than one long one — a ride offer, not a notification. */
private const val HAPTIC_GAP_MS = 150L

/** D2' §SCR-DA-014's *"strong haptic on arrival"*, as a waveform. */
private val HAPTIC_PATTERN = longArrayOf(HAPTIC_DELAY_MS, HAPTIC_BUZZ_MS, HAPTIC_GAP_MS, HAPTIC_BUZZ_MS)

/** How much of the surface a card keeps over the dark takeover. */
private const val CARD_TINT = 0.16f
