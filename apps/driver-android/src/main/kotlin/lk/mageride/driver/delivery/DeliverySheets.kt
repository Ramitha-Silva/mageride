package lk.mageride.driver.delivery

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Call
import androidx.compose.material.icons.outlined.MoveToInbox
import androidx.compose.material.icons.outlined.Outbox
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material.icons.outlined.Shield
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import lk.mageride.driver.R
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.PackageLabels
import lk.mageride.driver.ui.component.DashboardSheet
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.OtpEntry
import lk.mageride.driver.ui.component.OtpProgress
import lk.mageride.driver.ui.component.SolidBadge
import lk.mageride.driver.ui.component.StatusCta
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.CtaTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.domain.ride.PackageGateOutcome
import lk.mageride.shared.domain.ride.PackageHandoff

/**
 * **SCR-DA-016a · review & start** — sheet 1 of 3.
 *
 * What a driver decides on before they set off: how far the two legs are, how the parcel is being
 * paid for, and who is at each end with a button to ring them. **Cancel is not a cancellation of
 * the delivery** — it releases the job (see [DeliveryViewModel.cancel]) — which is why it is an
 * outlined error button beside the CTA rather than a destructive confirm.
 */
@Composable
internal fun DeliveryReviewSheet(state: DeliveryState, viewModel: DeliveryViewModel, modifier: Modifier = Modifier) {
    DashboardSheet(modifier = modifier) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.delivery_title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f),
            )
            state.ride?.packageSize?.let { size ->
                SolidBadge(label = stringResource(PackageLabels.size(size)), accent = MageRideTheme.vehicle.truck)
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            DistanceCard(
                label = R.string.delivery_leg_pickup,
                metres = state.pickupMetres,
                modifier = Modifier.weight(1f),
            )
            DistanceCard(
                label = R.string.delivery_leg_drop,
                metres = state.dropMetres,
                modifier = Modifier.weight(1f),
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.delivery_payment),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                text = stringResource(PackageLabels.payment(state.ride?.paymentMethod)),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f),
                textAlign = TextAlign.End,
            )
        }

        PartyRow(state = state, party = DeliveryParty.SENDER, onCall = viewModel::call)
        PartyRow(state = state, party = DeliveryParty.RECIPIENT, onCall = viewModel::call)

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            OutlinedButton(
                onClick = viewModel::cancel,
                modifier = Modifier
                    .weight(1f)
                    .height(CtaTokens.Height),
                enabled = !state.busy,
                shape = RoundedCornerShape(CtaTokens.Radius),
                colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
            ) {
                Text(text = stringResource(R.string.delivery_cancel), style = MaterialTheme.typography.titleMedium)
            }
            StatusCta(
                label = stringResource(R.string.delivery_start),
                accent = MageRideTheme.status.success,
                onClick = viewModel::advance,
                modifier = Modifier.weight(1f),
                enabled = state.canAdvance,
            )
        }
    }
}

/**
 * **SCR-DA-016b · pickup & OTP** — sheet 2 of 3.
 *
 * The sender's four digits are what release the parcel (P-07): a correct one fires
 * `package.picked_up`, which is also the event that sends the *recipient* their own code — by FCM
 * if they have an account and by SMS with a tracking link if they do not (AL-21, US-20.5).
 */
@Composable
internal fun DeliveryPickupSheet(state: DeliveryState, viewModel: DeliveryViewModel, modifier: Modifier = Modifier) {
    DashboardSheet(modifier = modifier) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Surface(
                modifier = Modifier.size(ControlTokens.AvatarSmall),
                shape = CircleShape,
                color = MaterialTheme.colorScheme.surfaceVariant,
            ) {
                Icon(
                    imageVector = Icons.Outlined.Outbox,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = stringResource(R.string.delivery_party_sender),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = state.ride?.pickup?.address
                        ?.let { stringResource(R.string.delivery_pickup_at, it) }
                        .orEmpty(),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            OutlinedButton(
                onClick = { viewModel.call(DeliveryParty.SENDER) },
                modifier = Modifier.weight(1f),
                enabled = state.phoneOf(DeliveryParty.SENDER) != null,
            ) {
                Icon(imageVector = Icons.Outlined.Call, contentDescription = null)
                Text(
                    text = stringResource(R.string.ride_call_sender),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }
            OutlinedButton(
                onClick = viewModel::openSos,
                modifier = Modifier.weight(1f),
                // SCR-DA-032 needs a fix to attach to the alarm — `POST /v1/sos` has no
                // positionless form. See `SosState.awaitingPosition`.
                enabled = state.position != null,
            ) {
                Icon(
                    imageVector = Icons.Outlined.Shield,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.error,
                )
                Text(
                    text = stringResource(R.string.ride_sos),
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }
        }

        OtpBlock(state = state, label = R.string.delivery_pickup_otp_label, onOtpChange = viewModel::onOtpChange)

        MageRideCta(
            label = stringResource(R.string.delivery_verify_pickup),
            onClick = viewModel::advance,
            enabled = state.canAdvance,
            loading = state.busy,
        )
    }
}

/**
 * **SCR-DA-016c · complete** — sheet 3 of 3.
 *
 * *"Delivery completed"* **replaces the old "Cash received (COD)"** (AL-33): the parcel changing
 * hands and the cash being counted are two different events, and coupling them meant a driver who
 * had delivered could not say so until the money was settled. Uncollected COD is reconciled
 * separately and becomes `Disputed` after 24 hours (P-14).
 *
 * The photograph is P-10's fallback for a recipient who is not there, and it **completes the
 * delivery on its own** — the receipt then reports `photo_proof` rather than `otp_verified`.
 */
@Composable
internal fun DeliveryCompleteSheet(state: DeliveryState, viewModel: DeliveryViewModel, modifier: Modifier = Modifier) {
    DashboardSheet(modifier = modifier) {
        Text(
            text = stringResource(R.string.delivery_complete_title),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )

        OtpBlock(state = state, label = R.string.delivery_delivery_otp_label, onOtpChange = viewModel::onOtpChange)

        PartyRow(state = state, party = DeliveryParty.SENDER, onCall = viewModel::call)
        PartyRow(state = state, party = DeliveryParty.RECIPIENT, onCall = viewModel::call)

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            OutlinedButton(
                onClick = viewModel::requestProofCapture,
                modifier = Modifier
                    .weight(1f)
                    .height(CtaTokens.Height),
                enabled = !state.busy,
                shape = RoundedCornerShape(CtaTokens.Radius),
            ) {
                Icon(imageVector = Icons.Outlined.PhotoCamera, contentDescription = null)
                Text(
                    text = stringResource(
                        if (state.proof == null) R.string.delivery_photo_proof else R.string.delivery_photo_retake,
                    ),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }
            StatusCta(
                label = stringResource(R.string.delivery_completed),
                accent = MageRideTheme.status.success,
                onClick = viewModel::advance,
                modifier = Modifier.weight(1f),
                enabled = state.canAdvance,
            )
        }
    }
}

/**
 * One of the wireframe's two `card fill` tiles — `Pickup · 1.2 km`.
 *
 * The distance is absent rather than zero before the first GNSS fix: `0.0 km` on a courier's review
 * sheet reads as "you are already there".
 */
@Composable
private fun DistanceCard(@StringRes label: Int, metres: Double?, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .background(
                MaterialTheme.colorScheme.surfaceVariant.copy(alpha = CARD_TINT),
                RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(MageRideTheme.spacing.sm),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = metres?.let(MoneyFormat::distance).orEmpty(),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/**
 * The wireframe's `listrow` for one end of the delivery, with **its own call button** (AL-33).
 *
 * The button is disabled rather than hidden when the ride carries no number: a missing row would
 * make the sheet look like a delivery with one party, and there are always two.
 */
@Composable
private fun PartyRow(
    state: DeliveryState,
    party: DeliveryParty,
    onCall: (DeliveryParty) -> Unit,
    modifier: Modifier = Modifier,
) {
    val phone = state.phoneOf(party)

    Row(
        modifier = modifier
            .fillMaxWidth()
            .background(
                MaterialTheme.colorScheme.surfaceVariant.copy(alpha = CARD_TINT),
                RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Icon(
            imageVector = if (party == DeliveryParty.SENDER) Icons.Outlined.Outbox else Icons.Outlined.MoveToInbox,
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = partyLabel(party, state.ride?.recipientName),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = phone.orEmpty(),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        OutlinedButton(
            onClick = { onCall(party) },
            modifier = Modifier
                .width(ControlTokens.CallButtonWidth)
                .height(ControlTokens.CallButtonHeight),
            enabled = phone != null,
            shape = RoundedCornerShape(MageRideTheme.radius.sm),
            // The default 24 dp horizontal padding would leave no room for the icon in a 48 dp box.
            contentPadding = PaddingValues(0.dp),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = MageRideTheme.status.success),
        ) {
            Icon(
                imageVector = Icons.Outlined.Call,
                contentDescription = stringResource(R.string.delivery_call_party),
                modifier = Modifier.size(ControlTokens.RowIcon),
            )
        }
    }
}

/**
 * The four boxes, and what the attempt budget has to say about them (P-07).
 *
 * Three states, and they are genuinely different messages: the gate is open and quiet, the gate has
 * eaten a wrong code and says how many are left, or the five are gone and the handoff is with the
 * admin queue — at which point the entry is disabled, because a sixth box would be a lie.
 */
@Composable
private fun OtpBlock(
    state: DeliveryState,
    @StringRes label: Int,
    onOtpChange: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        OtpEntry(
            value = state.otp,
            onValueChange = onOtpChange,
            length = PackageHandoff.OTP_LENGTH,
            enabled = !state.locked && !state.busy,
            isError = state.attemptsUsed > 0,
        )
        OtpProgress(value = state.otp, length = PackageHandoff.OTP_LENGTH)

        when (val outcome = state.outcome) {
            // The fifth wrong code is the one that raises the queue item, so the message is the
            // handoff's status and not a warning about the next attempt: ops resolve it from there
            // and the driver is not stuck with the parcel.
            is PackageGateOutcome.AdminQueue -> Text(
                text = stringResource(R.string.delivery_otp_locked),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
            )

            is PackageGateOutcome.Open -> if (state.attemptsUsed > 0) {
                Text(
                    text = stringResource(R.string.delivery_otp_attempts_left, outcome.attemptsRemaining),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MageRideTheme.status.warning,
                )
            }

            else -> Unit
        }
    }
}

/**
 * `Sender` / `Recipient · Sunethra`.
 *
 * **There is no sender name on the wire.** `RideDetail` gained `recipientName` in Δ C037 and no
 * counterpart for the account that booked the delivery, so the sender's row is its role alone
 * rather than a name borrowed from a field that means something else. Recorded as a spec gap in the
 * C071 handoff.
 */
@Composable
private fun partyLabel(party: DeliveryParty, recipientName: String?): String = when {
    party == DeliveryParty.SENDER -> stringResource(R.string.delivery_party_sender)
    recipientName.isNullOrBlank() -> stringResource(R.string.delivery_party_recipient)
    else -> stringResource(R.string.delivery_party_recipient) + DeliveryLabels.NAME_SEPARATOR + recipientName
}

/** The symbols on these sheets that are data rather than copy — the same rule `Rs` follows. */
internal object DeliveryLabels {

    /** The wireframe's `Recipient · Sunethra`. A middle dot reads the same in Si, Ta and En. */
    const val NAME_SEPARATOR: String = " · "
}

/** How much of the surface tint a `card fill` keeps, matching SCR-DA-014's own rows. */
private const val CARD_TINT = 0.4f
