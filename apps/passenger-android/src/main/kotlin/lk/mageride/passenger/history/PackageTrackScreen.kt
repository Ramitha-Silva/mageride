package lk.mageride.passenger.history

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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Call
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.map.MapPin
import lk.mageride.passenger.map.MapVehicle
import lk.mageride.passenger.map.VehicleLayers
import lk.mageride.passenger.ride.CallChoice
import lk.mageride.passenger.ride.CallChooserSheet
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * SCR-PA-020 and SCR-PA-021 — the parcel, from whichever end is holding the phone.
 *
 * The wireframes: a map with the driver's live marker, a **four-step bar** (`Pickup pending ·
 * Picked up · In transit · Delivered`), the party's own OTP, and the driver card with a Call.
 *
 * **Which of the two this is, is a fact about the ride and not about the link.**
 * `mageride://package/{rideId}` is the same URI for both parties — the sender gets it on
 * `package_delivered`, the recipient on `package_picked_up` — so the view model reads the ride and
 * decides. See [PackageParty].
 *
 * **A recipient never signed in to get here** (P-09, AL-45): they came from an FCM deep link, or —
 * with no app — from an SMS onto the SCR-WT web page, which is not this app's problem.
 */
@Composable
@Suppress("LongMethod") // The wireframe's layout tree: map, step bar, OTP, driver card.
internal fun PackageTrackScreen(
    onBack: () -> Unit,
    onFreeCall: () -> Unit,
    choice: CallChoice,
    model: PackageTrackViewModel,
) {
    val state by model.state.collectAsStateWithLifecycle()
    var callOpen by remember { mutableStateOf(false) }

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
                text = stringResource(
                    if (state.party == PackageParty.SENDER) {
                        R.string.package_track_sending
                    } else {
                        R.string.package_track_incoming
                    },
                ),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }

        Box(modifier = Modifier.weight(1f)) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                vehicles = state.driverPosition?.let {
                    listOf(MapVehicle(vehicleId = DRIVER_MARKER, lat = it.lat, lng = it.lng))
                }.orEmpty(),
                pins = listOfNotNull(
                    state.ride?.pickup?.let { MapPin(VehicleLayers.PIN_PICKUP, it.lat, it.lng) },
                    state.ride?.dropoff?.let { MapPin(VehicleLayers.PIN_DROPOFF, it.lat, it.lng) },
                ),
                camera = state.driverPosition?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
            )
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    MaterialTheme.colorScheme.background,
                    RoundedCornerShape(topStart = MageRideTheme.radius.card, topEnd = MageRideTheme.radius.card),
                )
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            StatusBar(step = state.step)

            OtpRow(state = state)

            state.ride?.driver?.let { driver ->
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = driver.name,
                            style = MaterialTheme.typography.bodyLarge,
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
                    OutlinedButton(onClick = { callOpen = true }) {
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
                }
            }

            state.error?.let { InlineError(message = stringResource(it)) }
        }
    }

    if (callOpen) {
        CallChooserSheet(
            driverName = state.ride?.driver?.name,
            // AL-48 — the real number, from `Accepted` onward. On a package `counterpartyPhone` is
            // the FAR end, which for a sender is the recipient; the driver is who this screen calls,
            // so it takes the ride's driver block and the number the contract carries.
            driverPhone = state.ride?.counterpartyPhone,
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

/** The wireframe's four-step bar (US-20.7). */
@Composable
private fun StatusBar(step: Int) {
    val labels: List<Int> = listOf(
        R.string.package_step_pending,
        R.string.package_step_picked_up,
        R.string.package_step_in_transit,
        R.string.package_step_delivered,
    )

    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        labels.forEachIndexed { index, label ->
            Column(modifier = Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(ControlTokens.SheetHandleHeight)
                        .background(
                            if (index <= step) {
                                MaterialTheme.colorScheme.primary
                            } else {
                                MaterialTheme.colorScheme.surfaceVariant
                            },
                            RoundedCornerShape(MageRideTheme.radius.sm),
                        ),
                )
                Text(
                    text = stringResource(label),
                    modifier = Modifier.padding(top = MageRideTheme.spacing.xxs),
                    style = MaterialTheme.typography.labelSmall,
                    color = if (index <= step) {
                        MaterialTheme.colorScheme.onSurface
                    } else {
                        MaterialTheme.colorScheme.onSurfaceVariant
                    },
                    textAlign = TextAlign.Center,
                )
            }
        }
    }
}

/**
 * The party's own handover code.
 *
 * **Or an honest absence.** Neither OTP can be read back from the platform — the pickup code is
 * returned once at booking and the delivery code arrives only on a push — so a screen opened
 * without one has nothing to show, and says so rather than drawing four empty boxes that look like
 * a loading state. See [PackageOtps].
 */
@Composable
private fun OtpRow(state: PackageTrackState) {
    if (state.delivered) return

    val label = if (state.party == PackageParty.SENDER) {
        R.string.package_pickup_otp_label
    } else {
        R.string.package_delivery_otp_label
    }

    Column {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = state.otp ?: stringResource(R.string.package_otp_unavailable),
            style = if (state.otp != null) {
                MaterialTheme.typography.headlineMedium
            } else {
                MaterialTheme.typography.bodyMedium
            },
            color = if (state.otp != null) {
                MaterialTheme.colorScheme.onSurface
            } else {
                MaterialTheme.colorScheme.onSurfaceVariant
            },
        )
    }
}

private const val SEPARATOR = " · "
private const val DRIVER_MARKER = "driver"
