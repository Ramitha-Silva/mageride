package lk.mageride.driver.ride

import android.content.Intent
import android.net.Uri
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Call
import androidx.compose.material.icons.outlined.Navigation
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.Shield
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverHomeMap
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.DashboardSheet
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.StatusCta
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideKind
import org.koin.androidx.compose.koinViewModel
import org.koin.core.parameter.parametersOf

/**
 * **SCR-DA-015 · the active ride** — `Accepted → DriverArrived → InProgress → Completed`.
 *
 * The wireframe: a navigation banner, the map with the pickup pin, and a sheet carrying the rider,
 * the route and the fare, a **Call rider** / **SOS** pair, and one big CTA whose label is the ride's
 * own next step.
 *
 * **The QR confirm sheet is not an extra** (AL-47, US-26.1). A ride the passenger paid by scanning
 * the driver's own bank LankaQR has no gateway callback that could settle it — the money moved
 * bank-to-bank and only the driver's banking app saw it — so *"QR payment received?"* **is** the
 * settlement, and confirming is what posts the earning (R-05).
 *
 * @param onFinished The ride reached a terminal state; the driver goes back to standby.
 */
@Composable
internal fun ActiveRideScreen(rideId: Ulid, onFinished: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: ActiveRideViewModel = koinViewModel { parametersOf(rideId) }
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    Column(modifier = modifier.fillMaxSize()) {
        DashboardBanner(
            text = stringResource(navigationHint(state.rideState)),
            accent = MaterialTheme.colorScheme.secondary,
            icon = Icons.Outlined.Navigation,
        )

        state.error?.let { message ->
            DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
        }

        Box(modifier = Modifier.weight(1f)) {
            // MAP-10's 100 m circle, drawn where the driver is being sent: the pickup until they
            // are on board, the drop afterwards. It is D5' §7's arrival geofence — the same circle
            // that decides `DriverArrived` — so the driver can see what they have to reach.
            DriverHomeMap(
                position = state.position,
                vehicleType = null,
                geofence = state.geofence,
                modifier = Modifier.fillMaxSize(),
            )
        }

        ActiveRideSheet(state = state, viewModel = viewModel)
    }

    RideDialogs(state = state, viewModel = viewModel)

    // AL-48's direct dial. The number is the counterparty's real MSISDN, revealed post-accept, and
    // on a proxy booking it is the RIDER's rather than the booker's (P-05) — ride-svc decides that,
    // not this screen.
    LaunchedEffect(state.dialNumber) {
        state.dialNumber?.let { number ->
            runCatching {
                context.startActivity(
                    Intent(Intent.ACTION_DIAL, Uri.parse("tel:$number")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                )
            }
            viewModel.consume()
        }
    }

    LaunchedEffect(state.finished) {
        if (state.finished) onFinished()
    }
}

/** The wireframe's sheet: rider, route, the two icon buttons, and the state's own CTA. */
@Composable
private fun ActiveRideSheet(state: ActiveRideState, viewModel: ActiveRideViewModel, modifier: Modifier = Modifier) {
    val ride = state.ride

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
                    imageVector = Icons.Outlined.Person,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.RowIcon),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = ride?.riderName ?: stringResource(R.string.ride_rider),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = listOfNotNull(
                        ride?.pickup?.address,
                        ride?.dropoff?.address,
                    ).joinToString(RideLabels.ROUTE_ARROW) +
                        (ride?.fare?.amountMinor?.let { " · ${MoneyFormat.rupees(it)}" }.orEmpty()),
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
                onClick = { viewModel.open(RideSheet.CALL_TYPE) },
                modifier = Modifier.weight(1f),
                enabled = ride?.counterpartyPhone != null,
            ) {
                Icon(imageVector = Icons.Outlined.Call, contentDescription = null)
                Text(
                    text = stringResource(
                        if (ride?.kind == RideKind.PACKAGE) R.string.ride_call_sender else R.string.ride_call,
                    ),
                    modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                )
            }
            OutlinedButton(
                onClick = viewModel::triggerSos,
                modifier = Modifier.weight(1f),
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

        // D2' §SCR-DA-015's table colours the two ends of the ride by consequence: Start is
        // `success`, End is `error`. Arriving is neither — it is the ordinary primary CTA.
        val cta = forwardAction(state)
        when {
            cta != null && state.rideState == RideState.Accepted -> MageRideCta(
                label = stringResource(cta),
                onClick = viewModel::advance,
                loading = state.busy,
            )

            cta != null -> StatusCta(
                label = stringResource(cta),
                accent = if (state.rideState == RideState.InProgress) {
                    MaterialTheme.colorScheme.error
                } else {
                    MageRideTheme.status.success
                },
                onClick = viewModel::advance,
                enabled = !state.busy,
            )

            state.awaitingQrConfirm -> MageRideCta(
                label = stringResource(R.string.ride_qr_title),
                onClick = { viewModel.open(RideSheet.QR_CONFIRM) },
                loading = state.busy,
            )
        }
    }
}

/**
 * The CTA's label, from the ride's own state.
 *
 * `null` means there is no forward move from here — a terminal ride, or a driver-QR ride waiting on
 * AL-47's confirm, which is a different button.
 */
@StringRes
private fun forwardAction(state: ActiveRideState): Int? = when (state.rideState) {
    RideState.Accepted -> R.string.ride_arrived
    RideState.DriverArrived -> R.string.ride_start
    RideState.InProgress -> R.string.ride_complete
    else -> null
}

/** The banner above the map — where the driver is being sent next. */
@StringRes
private fun navigationHint(state: RideState?): Int = when (state) {
    RideState.Accepted, RideState.DriverArrived -> R.string.ride_navigate_pickup
    RideState.InProgress -> R.string.ride_navigate_drop
    else -> R.string.ride_finished
}

/** Symbols rather than sentences — the same rule `Rs` and `L3` follow (C068). */
internal object RideLabels {

    /** The wireframe's `Galle Face → Nugegoda`. An arrow reads the same in Si, Ta and En. */
    const val ROUTE_ARROW: String = " → "
}
