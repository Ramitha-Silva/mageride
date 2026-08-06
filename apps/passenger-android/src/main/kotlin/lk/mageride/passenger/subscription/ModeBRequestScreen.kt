package lk.mageride.passenger.subscription

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.home.VehicleLabels
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.LabelledTextField
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.ServiceMode

/**
 * SCR-PA-024 — "Private transport", the Mode B access request.
 *
 * The wireframe: a `MODE B` badge card explaining that *"the owner approves in the Driver App"*, a
 * **Vehicle ID** field, the Pending/Accepted/Rejected chip, and a **Send request** CTA pinned to
 * the bottom.
 *
 * **Two doors, one screen** (AL-23). A Mode B marker on SCR-PA-010 hands over the vehicle id it
 * just drew; the drawer's *"Private transport"* row opens the same screen with an empty field. The
 * route argument is optional for exactly that reason — see `PassengerRoute.ModeBRequest`.
 *
 * **A Mode B marker never opens SCR-PA-007.** That popup is Mode A's; a private vehicle a
 * passenger has no grant for has nothing to show them but this.
 */
@Composable
internal fun ModeBRequestScreen(onBack: () -> Unit, onOpenSubscriptions: () -> Unit, model: ModeBRequestViewModel) {
    val state by model.state.collectAsStateWithLifecycle()

    Column(modifier = Modifier.fillMaxSize()) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.action_back),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = stringResource(R.string.mode_b_request_title),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            ExplainerCard()

            LabelledTextField(
                label = stringResource(R.string.mode_b_vehicle_id),
                value = state.vehicleId,
                onValueChange = model::onVehicleIdChange,
                placeholder = VEHICLE_ID_EXAMPLE,
                enabled = !state.sending && state.existing == null,
                imeAction = ImeAction.Done,
            )

            Text(
                text = stringResource(
                    if (state.prefilled) R.string.mode_b_from_marker else R.string.mode_b_type_id,
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            state.status?.let { DecisionCard(status = it, onOpenSubscriptions = onOpenSubscriptions) }

            state.error?.let { InlineError(message = stringResource(it)) }

            // The wireframe's `spacer` — the CTA is pinned to the bottom of the body (US-24.1's
            // rule for SCR-PA-002, applied here because the wireframe draws the same layout).
            Spacer(modifier = Modifier.weight(1f))

            MageRideCta(
                label = stringResource(R.string.mode_b_send_request),
                onClick = model::send,
                enabled = state.canSend,
                loading = state.sending,
            )
        }
    }
}

/** The wireframe's filled `MODE B` card. */
@Composable
private fun ExplainerCard() {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(
                MaterialTheme.colorScheme.surfaceVariant,
                RoundedCornerShape(MageRideTheme.radius.md),
            )
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            // C078's own mode copy, not a second key for the same words — D2' §0.2 has one Mode B
            // label and `StringResourceTest` reads two identical values as a translation nobody did.
            text = stringResource(VehicleLabels.modeName(ServiceMode.B)),
            modifier = Modifier
                .background(MageRideTheme.mode.modeB, RoundedCornerShape(MageRideTheme.radius.sm))
                .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onPrimary,
        )
        Text(
            text = stringResource(R.string.mode_b_explainer),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * US-4.6's chip, as the wireframe's tinted card.
 *
 * **Pending is the state this screen can actually observe.** Accepted is inferred from a
 * subscription existing (see [ModeBRequestViewModel]); Rejected has no signal on this surface at
 * all and is drawn only because the enum can carry it — a rejection reaches a passenger through
 * the owner, not through the platform. Recorded in the C082 handoff.
 */
@Composable
private fun DecisionCard(status: AccessRequestStatus, onOpenSubscriptions: () -> Unit) {
    val tint = when (status) {
        AccessRequestStatus.PENDING -> MageRideTheme.status.warning
        AccessRequestStatus.ACCEPTED -> MageRideTheme.status.success
        AccessRequestStatus.REJECTED -> MaterialTheme.colorScheme.error
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(tint.copy(alpha = CARD_TINT), RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            text = stringResource(
                when (status) {
                    AccessRequestStatus.PENDING -> R.string.mode_b_status_pending
                    AccessRequestStatus.ACCEPTED -> R.string.mode_b_status_accepted
                    AccessRequestStatus.REJECTED -> R.string.mode_b_status_rejected
                },
            ),
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = stringResource(
                when (status) {
                    // US-4.7's first month free, which is the fleet's offer and not the platform's.
                    AccessRequestStatus.PENDING -> R.string.mode_b_pending_note

                    AccessRequestStatus.ACCEPTED -> R.string.mode_b_accepted_note

                    AccessRequestStatus.REJECTED -> R.string.mode_b_rejected_note
                },
            ),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (status == AccessRequestStatus.ACCEPTED) {
            MageRideTextLink(
                label = stringResource(R.string.mode_b_open_subscriptions),
                onClick = onOpenSubscriptions,
            )
        }
    }
}

/** The wireframe's status cards are a wash of their own colour, not a solid fill. */
private const val CARD_TINT = 0.16f

/**
 * The wireframe's example Vehicle ID, as the field's placeholder.
 *
 * A registration handle is the same characters in all three scripts — the same argument
 * `LanguageNames` and `PhoneNumber` make for the language endonyms and the `+94` prefix — so it is
 * a Kotlin constant rather than three identical `strings.xml` values, which `StringResourceTest`
 * reads as a translation nobody did.
 */
private const val VEHICLE_ID_EXAMPLE = "MR-VEH-48213"
