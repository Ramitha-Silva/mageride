package lk.mageride.passenger.home

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.domain.geo.distanceMetres
import lk.mageride.shared.realtime.VehicleFrame
import kotlin.math.roundToInt

/**
 * SCR-PA-007 — the Mode A vehicle popup.
 *
 * **Mode A only, and that is the fence rather than a layout choice** (AL-23, US-7.4). A Mode B
 * marker routes to SCR-PA-024 and a Mode C marker does nothing; neither ever reaches this
 * composable, because [LiveMapViewModel.onMarkerTapped] decides by mode before a sheet exists.
 *
 * **The distance is computed here, the ETA is the server's.** `VehicleFrame` is a position and
 * carries neither — so the popup measures the straight-line distance itself with `:shared`'s
 * haversine, and the ETA, driver and plate arrive separately as a [VehicleDetail] from
 * `GET /v1/nearby`. **A straight line is not a road**, so an ETA derived from the distance here
 * would be a number a passenger plans around and the platform never promised; until the lookup
 * lands, or when it fails, the field says so.
 *
 * **The route is the one field nothing can answer.** The wireframe's headline is
 * *"Route 138 — Pettah → Maharagama"*, and neither `VehicleFrame` nor `NearbyVehicle` carries a
 * route number — see the C078 handoff, which files it as a contract gap rather than inventing one.
 *
 * **The driver's name is Mode A's alone** (US-7.12). A bus driver is public information; a Mode C
 * driver's name is not the passenger's to see until the ride is accepted — and no Mode C vehicle
 * reaches this composable at all.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun VehiclePopup(vehicle: VehicleFrame, detail: VehicleDetail?, around: GeoPoint?, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.lg),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                ModeBadge(vehicle)
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                val type = vehicle.type
                Box(
                    modifier = Modifier
                        .size(ControlTokens.AvatarSmall)
                        .background(
                            color = type?.let { VehicleLabels.colour(MageRideTheme.vehicle, it) }
                                ?: MageRideTheme.vehicle.private,
                            shape = CircleShape,
                        ),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        imageVector = VehicleLabels.icon(type ?: VehicleType.BUS),
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.RowIcon),
                        tint = MaterialTheme.colorScheme.onPrimary,
                    )
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        // The route is what a passenger tapped a bus to find out, and
                        // `VehicleFrame` does not carry one — see the C078 handoff. Until it does,
                        // the type is the honest headline rather than an invented route number.
                        text = type?.let { stringResource(VehicleLabels.name(it)) }
                            ?: stringResource(R.string.vehicle_unknown),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    // The wireframe's "NB-4521 · Driver: K. Perera". The plate is the vehicle's
                    // public identity and the id is the fallback, because a passenger comparing
                    // what is on screen to what is in front of them reads a number plate.
                    Text(
                        text = subtitle(detail) ?: vehicle.vehicleId,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                MetricCard(
                    label = stringResource(R.string.popup_distance),
                    value = distanceLabel(around, vehicle),
                    modifier = Modifier.weight(1f),
                )
                MetricCard(
                    label = stringResource(R.string.popup_eta),
                    value = etaLabel(detail),
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

/** The wireframe's `MODE A · BUS` badge. */
@Composable
private fun ModeBadge(vehicle: VehicleFrame) {
    val label = vehicle.type
        ?.let { "${VehicleLabels.modeBadge(ServiceMode.A)} · ${stringResource(VehicleLabels.name(it))}" }
        ?: VehicleLabels.modeBadge(ServiceMode.A)

    Text(
        text = label,
        modifier = Modifier
            .background(MageRideTheme.mode.modeA, RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onPrimary,
    )
}

/** One of the wireframe's two `card fill` tiles. */
@Composable
private fun MetricCard(label: String, value: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.md))
            .padding(MageRideTheme.spacing.sm),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * Straight-line metres or kilometres to the vehicle.
 *
 * `:shared`'s `distanceMetres` is the haversine C017 uses for every exact-distance rule on the
 * platform, so the number here is the same one dispatch would compute — which is the point of
 * having one implementation. It is a **straight line** and is labelled as a distance, never
 * converted into a time.
 */
@Composable
private fun distanceLabel(around: GeoPoint?, vehicle: VehicleFrame): String {
    if (around == null) return stringResource(R.string.popup_distance_unavailable)
    val metres = distanceMetres(around, vehicle.point).roundToInt()
    return if (metres < METRES_IN_KM) {
        stringResource(R.string.popup_distance_m, metres)
    } else {
        stringResource(R.string.popup_distance_km, metres / METRES_IN_KM, (metres % METRES_IN_KM) / KM_TENTH)
    }
}

/**
 * The wireframe's second line — plate, driver, or both.
 *
 * `null` when the lookup has not landed or came back with neither, so the caller falls back to the
 * vehicle id rather than drawing an empty row where a plate should be.
 */
@Composable
private fun subtitle(detail: VehicleDetail?): String? {
    val plate = detail?.registrationNumber?.takeIf { it.isNotBlank() }
    val driver = detail?.driverName
        ?.takeIf { it.isNotBlank() }
        ?.let { stringResource(R.string.popup_driver, it) }
    return listOfNotNull(plate, driver).takeIf { it.isNotEmpty() }?.joinToString(SEPARATOR)
}

/**
 * Minutes to the vehicle, rounded up.
 *
 * Up rather than to-nearest: a bus that is 89 seconds away is "2 min", and a passenger who reads
 * "1 min" and misses it by twenty seconds was told the wrong thing. Under a minute is its own
 * string — "0 min" is not an arrival.
 */
@Composable
private fun etaLabel(detail: VehicleDetail?): String = when (val seconds = detail?.etaSeconds) {
    null -> stringResource(R.string.popup_eta_unavailable)
    in 0 until SECONDS_IN_MINUTE -> stringResource(R.string.popup_eta_now)
    else -> stringResource(R.string.popup_eta_minutes, (seconds + SECONDS_IN_MINUTE - 1) / SECONDS_IN_MINUTE)
}

private const val METRES_IN_KM = 1_000
private const val KM_TENTH = 100
private const val SECONDS_IN_MINUTE = 60

/** The wireframe's `·` between a plate and a driver's name. Punctuation, not copy. */
private const val SEPARATOR = " · "
