package lk.mageride.passenger.booking

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.DirectionsWalk
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.passenger.home.VehicleLabels
import lk.mageride.passenger.ride.PaymentRails
import lk.mageride.passenger.ui.MoneyFormat
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.transit.TransitOptionKind

// SCR-PA-009's two lists, split out of `RideBookingScreen.kt` so neither file is a wall.
//
// The screen is the layout — map, summary, toggles, CTA — and this is what goes between: a GTFS
// section that degrades to one muted row (AL-55), and a tier row that can show a price and nothing
// else (AL-19). Both are the fences, drawn.

/**
 * The GTFS half.
 *
 * **AL-55's degradation is a muted row, not an error.** D2' §SCR-PA-009: when transit-svc has no
 * feed for the corridor or is unreachable, the section becomes *"Bus route info coming soon for
 * this area"* and everything else on the screen keeps working. Nothing blocks on GTFS coverage.
 */
@Composable
internal fun PublicSection(state: RideBookingState, onSelect: (PublicRouteRow) -> Unit) {
    SectionLabel(text = stringResource(R.string.booking_public_section))

    when {
        state.routesLoading -> Loading(stringResource(R.string.booking_loading_routes))

        state.publicUnavailable -> MutedRow(stringResource(R.string.booking_no_gtfs))

        state.routes.isEmpty() -> MutedRow(stringResource(R.string.booking_no_routes))

        else -> state.routes.forEach { route ->
            PublicRouteCard(
                route = route,
                selected = (state.selection as? BookingSelection.Public)?.route == route,
                onSelect = { onSelect(route) },
            )
        }
    }

    // "⓵ Walk 250 m to Pamankada halt — blue route on map", shown only when off-route.
    state.walkHalt?.let { hint ->
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Icon(
                imageVector = Icons.AutoMirrored.Filled.DirectionsWalk,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.ChipIcon),
                tint = MaterialTheme.colorScheme.primary,
            )
            Text(
                text = stringResource(R.string.booking_walk_hint, hint.metres, hint.haltName),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** `🚌 138 · Pettah → Maharagama | via High Level Rd | Direct | PUBLIC`. */
@Composable
internal fun PublicRouteCard(route: PublicRouteRow, selected: Boolean, onSelect: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onSelect)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        RadioButton(selected = selected, onClick = onSelect)
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = listOfNotNull(
                    route.routeShortName.takeIf { it.isNotBlank() },
                    route.headsign,
                ).joinToString(ROUTE_SEPARATOR),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            route.description?.takeIf { it.isNotBlank() }?.let {
                Text(
                    text = it,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (route.transfers > 0) {
                Text(
                    text = stringResource(R.string.booking_transfers, route.transfers),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        Tag(
            text = stringResource(
                if (route.kind ==
                    TransitOptionKind.DIRECT
                ) {
                    R.string.booking_tag_direct
                } else {
                    R.string.booking_tag_transit
                },
            ),
        )
        Tag(text = stringResource(R.string.booking_tag_public))
    }
}

/**
 * One Mode C tier — **type, "Standby · upfront fare", and a price. Nothing else.**
 *
 * AL-19 / BR-23.3: no minutes-away, no distance to driver, because no driver has been matched. The
 * card could not show one if it wanted to — [TierQuote] has no such field.
 */
@Composable
internal fun TierRow(quote: TierQuote, selected: Boolean, onSelect: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onSelect)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        RadioButton(selected = selected, onClick = onSelect)
        Icon(
            imageVector = VehicleLabels.icon(quote.vehicleType.asVehicleType()),
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(VehicleLabels.name(quote.vehicleType.asVehicleType())),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(R.string.booking_standby),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            text = MoneyFormat.rupees(quote.amountMinor),
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
    }
}

/** The wireframe's `Payment: Cash ▾` chip. C080's SCR-PA-016 is the full picker. */
@Composable
internal fun PaymentChip(method: PaymentMethod, onChange: (PaymentMethod) -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        // The three rails that survive AL-57/AL-59, and no surcharge on any of them. COD is
        // absent because it is package-only (AL-22, US-20.8) — SCR-PA-012 has its own row.
        PaymentRails.RIDE.forEach { option ->
            ToggleChip(
                label = stringResource(PaymentRails.label(option)),
                selected = option == method,
                onClick = { onChange(option) },
            )
        }
    }
}

/** `Direct` / `Transit` / `PUBLIC`. */
@Composable
internal fun Tag(text: String) {
    Text(
        text = text,
        modifier = Modifier
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xxs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

@Composable
internal fun Loading(text: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        CircularProgressIndicator(modifier = Modifier.size(ControlTokens.ChipIcon))
        Text(
            text = text,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
internal fun MutedRow(text: String) {
    Text(
        text = text,
        modifier = Modifier.padding(vertical = MageRideTheme.spacing.xs),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

/**
 * The bookable subset back to the full AL-09 type, so the tier row can reuse C078's icon and name
 * tables rather than growing a second copy of §0.2's vehicle list.
 */
internal fun RideVehicleType.asVehicleType(): VehicleType = when (this) {
    RideVehicleType.MOTORBIKE -> VehicleType.MOTORBIKE
    RideVehicleType.THREE_WHEELER -> VehicleType.THREE_WHEELER
    RideVehicleType.FLEX -> VehicleType.FLEX
    RideVehicleType.SEDAN -> VehicleType.SEDAN
    RideVehicleType.MINI_VAN -> VehicleType.MINI_VAN
    RideVehicleType.VAN -> VehicleType.VAN
    RideVehicleType.TRUCK -> VehicleType.TRUCK
    RideVehicleType.MINI_TRUCK -> VehicleType.MINI_TRUCK
}

/** The wireframe's `·` between a route number and its headsign. Punctuation, not copy. */
private const val ROUTE_SEPARATOR = " · "
