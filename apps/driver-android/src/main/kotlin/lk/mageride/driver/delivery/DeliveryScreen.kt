package lk.mageride.driver.delivery

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Navigation
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverHomeMap
import lk.mageride.driver.ui.MoneyFormat
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.shared.data.models.Ulid
import org.koin.androidx.compose.koinViewModel
import org.koin.core.parameter.parametersOf

/**
 * **SCR-DA-016a/b/c · package delivery** — the three sequential bottom sheets (AL-33, US-20.12/13).
 *
 * One screen, three sheets, because the wireframe draws one map with a sheet that changes over it:
 * *review the job* → *collect the parcel* → *hand it over*. Which sheet is up is the ride's own
 * business (`package.picked_up` is the `→ InProgress` move), so nothing here counts steps.
 *
 * **It is reached through `DriverRoute.ActiveRide`, like every other live job.** A delivery is a
 * ride — R-01 keeps one aggregate and `PushRouter` already lands `mageride://package/{id}` and
 * `mageride://ride/{id}` on the same destination — so SCR-DA-015 swaps to this screen once it reads
 * a `RideKind.PACKAGE`, in the same way Home swaps its sheet for a Mode A/B vehicle (C070).
 *
 * @param onFinished The parcel is handed over, or the job was released. Back to standby.
 */
@Composable
internal fun DeliveryScreen(rideId: Ulid, onFinished: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: DeliveryViewModel = koinViewModel { parametersOf(rideId) }
    val state by viewModel.state.collectAsStateWithLifecycle()

    Column(modifier = modifier.fillMaxSize()) {
        // Sheet 2's `banner info` — "Navigate to pickup · 1.2 km". The wireframe also prints a
        // "4 min", and there is no ETA on this surface to print: AL-19 keeps an arrival time off a
        // tier, and `RideDriver.etaSeconds` is dispatch's estimate of the driver for the PASSENGER.
        // A fabricated minute count on a courier's screen is worse than a distance alone.
        if (state.sheet == DeliverySheet.PICKUP) {
            DashboardBanner(
                text = state.pickupMetres
                    ?.let { stringResource(R.string.delivery_navigate_pickup_distance, MoneyFormat.distance(it)) }
                    // SCR-DA-015's own banner copy, because it is the same sentence — a second
                    // key with the same words in three languages is what drifts apart.
                    ?: stringResource(R.string.ride_navigate_pickup),
                accent = MaterialTheme.colorScheme.secondary,
                icon = Icons.Outlined.Navigation,
            )
        }

        state.error?.let { message ->
            DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
        }

        Box(modifier = Modifier.weight(1f)) {
            // MAP-10's 100 m circle, drawn where the driver is being sent: the sender's door until
            // the parcel is aboard, the recipient's afterwards.
            DriverHomeMap(
                position = state.position,
                vehicleType = null,
                geofence = state.geofence,
                modifier = Modifier.fillMaxSize(),
            )

            // Sheets 1 and 3 are `overlay sheet`s in the wireframe — the map behind them is dimmed,
            // because at those two moments the driver is reading the sheet and not the map. Sheet 2
            // is the one they navigate on, and it is not dimmed.
            if (state.sheet != DeliverySheet.PICKUP) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(MaterialTheme.colorScheme.scrim.copy(alpha = SCRIM_ALPHA)),
                )
            }
        }

        when (state.sheet) {
            DeliverySheet.REVIEW -> DeliveryReviewSheet(state = state, viewModel = viewModel)
            DeliverySheet.PICKUP -> DeliveryPickupSheet(state = state, viewModel = viewModel)
            DeliverySheet.COMPLETE -> DeliveryCompleteSheet(state = state, viewModel = viewModel)
        }
    }

    // AL-33's direct dial. Not AL-48's chooser: a call button on a delivery sheet places a real
    // mobile-phone call to whichever end of the delivery was tapped, and there is nothing to pick.
    val context = LocalContext.current
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

/** D2' §0.2's scrim opacity, the same one SCR-DA-010's offline map wears. */
private const val SCRIM_ALPHA = 0.45f
