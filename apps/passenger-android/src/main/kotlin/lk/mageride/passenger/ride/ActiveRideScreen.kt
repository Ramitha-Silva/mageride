package lk.mageride.passenger.ride

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Call
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.map.MapPin
import lk.mageride.passenger.map.MapVehicle
import lk.mageride.passenger.map.VehicleLayers
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-015 — the ride, while it is happening.
 *
 * The wireframe: a map with the driver's live marker, *"Driver arriving · 3 min"*, the driver card
 * (`👤 K. Fernando ★4.8 · Sedan · ABC-1234`), the **Start OTP** to read out, and
 * `📞 Call ▾ | ⛨ SOS | ✕`.
 *
 * **Cancelling here costs Rs 50 and the dialog says so before the tap** (US-6A.10, D-05). The debt
 * settles on the *next* trip rather than being charged now, which is precisely why it has to be
 * stated: nothing appears on a statement today, and an unwarned passenger meets it weeks later.
 *
 * **Payment is not a button on this screen, and it is now reached from it.** The ride moves to
 * `Completed` server-side and the ride-state change is what carries the passenger to SCR-PA-016 — a
 * *"pay now"* control on a moving vehicle would be an invitation to settle a fare that is not final
 * yet. Δ C098: that hand-off was documented here and **implemented nowhere**, so a completed ride
 * left the passenger on a finished trip with D-10 unreachable. See [RideHandOff].
 *
 * **`📞 Call ▾` opens SCR-PA-015a rather than dialling.** Free VoIP and a direct cellular call are
 * genuinely different things — one costs the passenger minutes and shows their number — so the
 * choice is theirs and is remembered (AL-48).
 *
 * **`⛨ SOS` navigates to SCR-PA-029 rather than raising the alarm here** (Δ C084). The confirm, the
 * countdown, the contact list and the dispatched state are that screen's, and `POST /v1/sos` having
 * one caller is what stops one emergency arriving on the operator's live feed as two events.
 */
@Composable
@Suppress("LongMethod") // The wireframe's layout tree: map, driver card, OTP, three actions.
internal fun ActiveRideScreen(
    onFinished: () -> Unit,
    onPayFare: () -> Unit,
    onReceipt: () -> Unit,
    onFreeCall: () -> Unit,
    onSos: () -> Unit,
    choice: CallChoice,
    model: ActiveRideViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()
    var callOpen by remember { mutableStateOf(false) }

    LaunchedEffect(state.handOff) {
        state.handOff?.route(onPayFare = onPayFare, onReceipt = onReceipt, onFinished = onFinished)
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Box(modifier = Modifier.weight(1f)) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                // The assigned driver, from `DriverMoved` on the `ride:{rideId}` group (US-6A.12).
                vehicles = state.driverPosition?.let {
                    listOf(MapVehicle(vehicleId = DRIVER_MARKER, lat = it.lat, lng = it.lng))
                }.orEmpty(),
                pins = listOfNotNull(
                    state.ride?.pickup?.let { MapPin(VehicleLayers.PIN_PICKUP, it.lat, it.lng) },
                    state.ride?.dropoff?.let { MapPin(VehicleLayers.PIN_DROPOFF, it.lat, it.lng) },
                ),
                camera = state.driverPosition?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
            )
            state.ride?.driver?.etaSeconds?.let { seconds ->
                Text(
                    text = stringResource(R.string.ride_arriving, seconds / SECONDS_PER_MINUTE),
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .padding(MageRideTheme.spacing.xs)
                        .background(
                            MaterialTheme.colorScheme.surfaceVariant,
                            RoundedCornerShape(MageRideTheme.radius.lg),
                        )
                        .padding(horizontal = MageRideTheme.spacing.sm, vertical = MageRideTheme.spacing.xxs),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    MaterialTheme.colorScheme.background,
                    RoundedCornerShape(
                        topStart = MageRideTheme.radius.card,
                        topEnd = MageRideTheme.radius.card,
                    ),
                )
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            DriverCard(state)

            // The wireframe's "Start OTP — give to driver". Read out, never typed here: the driver
            // enters it on SCR-DA-015, which is what proves the right passenger got in.
            state.ride?.let {
                Text(
                    text = stringResource(R.string.ride_start_otp),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                OutlinedButton(onClick = { callOpen = true }, modifier = Modifier.weight(1f)) {
                    Icon(
                        imageVector = Icons.Filled.Call,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.ride_call),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
                OutlinedButton(onClick = onSos, modifier = Modifier.weight(1f)) {
                    Icon(
                        imageVector = Icons.Filled.Shield,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                    Text(
                        text = stringResource(R.string.ride_sos),
                        modifier = Modifier.padding(start = MageRideTheme.spacing.xxs),
                    )
                }
                OutlinedButton(onClick = model::shareTrip) {
                    Icon(
                        imageVector = Icons.Filled.Share,
                        contentDescription = stringResource(R.string.ride_share),
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                }
                OutlinedButton(onClick = model::askToCancel) {
                    Icon(
                        imageVector = Icons.Filled.Close,
                        contentDescription = stringResource(R.string.ride_cancel),
                        modifier = Modifier.size(ControlTokens.ChipIcon),
                    )
                }
            }

            state.error?.let { InlineError(message = stringResource(it)) }
        }
    }

    if (state.pendingCancel) {
        CancelDialog(
            free = state.cancelIsFree,
            onConfirm = model::confirmCancel,
            onDismiss = model::dismissCancel,
        )
    }

    if (callOpen) {
        CallChooserSheet(
            driverName = state.ride?.driver?.name,
            driverPhone = state.driverPhone,
            choice = choice,
            onFreeCall = {
                callOpen = false
                onFreeCall()
            },
            onDialled = { },
            onDismiss = { callOpen = false },
        )
    }
}

/**
 * Where a hand-off sends the passenger (Δ C098).
 *
 * A function rather than a `when` inside the `LaunchedEffect`, because detekt's cyclomatic ceiling
 * for a composable is fifteen and this screen was already at it — which is the same reason the
 * cancel dialog and the driver card are their own composables.
 */
private fun RideHandOff.route(onPayFare: () -> Unit, onReceipt: () -> Unit, onFinished: () -> Unit) = when (this) {
    RideHandOff.Payment -> onPayFare()
    RideHandOff.Receipt -> onReceipt()
    RideHandOff.Finished -> onFinished()
}

/** `👤 K. Fernando ★4.8 · Sedan · ABC-1234 · 3 min`. */
@Composable
private fun DriverCard(state: ActiveRideState) {
    val driver = state.ride?.driver ?: return
    Column {
        Text(
            text = driver.name,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = listOfNotNull(
                driver.registrationNumber,
                driver.rating?.let { stringResource(R.string.ride_rating, it) },
            ).joinToString(SEPARATOR),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * US-6A.10's confirm.
 *
 * **The number is in the dialog, not in a help article.** D-05 carries the debt to the next trip,
 * so a passenger who cancels today sees nothing on a statement — and the only moment they can be
 * told what it costs is before they agree to it.
 */
@Composable
private fun CancelDialog(free: Boolean, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.ride_cancel_title)) },
        text = {
            Text(
                if (free) {
                    stringResource(R.string.ride_cancel_free)
                } else {
                    stringResource(R.string.ride_cancel_penalty, MoneyFormat.rupees(PENALTY_MINOR))
                },
            )
        },
        confirmButton = { TextButton(onClick = onConfirm) { Text(stringResource(R.string.ride_cancel_yes)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.ride_cancel_no)) } },
    )
}

/**
 * D-05's Rs 50, in minor units.
 *
 * Stated locally because the dialog has to name it **before** the call that would return it —
 * `CancelRideResponse.penalty` arrives after the cancel has happened, which is too late to be a
 * warning. The server's figure is what is actually owed and is what the screen reports afterwards.
 */
private const val PENALTY_MINOR = 5_000L

private const val SECONDS_PER_MINUTE = 60
private const val SEPARATOR = " · "

/** The single marker id the driver's live position is drawn under. */
private const val DRIVER_MARKER = "driver"
