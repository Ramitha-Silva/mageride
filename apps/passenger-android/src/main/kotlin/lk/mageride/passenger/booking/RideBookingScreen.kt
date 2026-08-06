package lk.mageride.passenger.booking

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.map.MageRideMap
import lk.mageride.passenger.map.MapCamera
import lk.mageride.passenger.map.MapPin
import lk.mageride.passenger.map.VehicleLayers
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import org.koin.androidx.compose.koinViewModel

/**
 * SCR-PA-009 — the multimodal list, and the primary screen of cluster 3.
 *
 * The wireframe: a map with the route and walk lines over a sheet carrying the pickup/drop summary,
 * the two toggles, the **Public buses · direct routes (GTFS)** section, the **Private (Mode C ·
 * standby)** tiers, and one CTA that says *"Track Route"* or *"Book Now"* depending on which list
 * was chosen from.
 *
 * **Two fences are visible in this file.**
 *
 * - **AL-19.** [TierRow] renders `quote.amountMinor` and there is nothing else on a [TierQuote] to
 *   render — no ETA, no distance. The type is the enforcement; this composable simply cannot show
 *   what it was not given.
 * - **AL-18.** A public row shows route number, description and the Direct/Transit tag, and
 *   selecting one hides the payment chip entirely, because no fare is charged for a bus.
 */
@Composable
@Suppress("LongMethod") // The wireframe's layout tree: map, summary, toggles, two lists, CTA.
internal fun RideBookingScreen(
    onBack: () -> Unit,
    onEditRoute: () -> Unit,
    onProxyDetails: () -> Unit,
    onPackageBooking: () -> Unit,
    onSchedule: () -> Unit,
    onBooked: (String) -> Unit,
    onTrackRoute: () -> Unit,
    model: RideBookingViewModel = koinViewModel(),
) {
    val state by model.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.booked) {
        state.booked?.let {
            onBooked(it)
            model.onBookingConsumed()
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        Box(modifier = Modifier.weight(MAP_WEIGHT)) {
            MageRideMap(
                modifier = Modifier.fillMaxSize(),
                routePolyline = state.routePolyline,
                walkPolyline = state.walkPolyline,
                pins = listOfNotNull(
                    state.draft.pickup?.let { MapPin(VehicleLayers.PIN_PICKUP, it.lat, it.lng) },
                    state.draft.dropoff?.let { MapPin(VehicleLayers.PIN_DROPOFF, it.lat, it.lng) },
                ),
                camera = state.draft.pickup?.let { MapCamera(it.lat, it.lng) } ?: MapCamera.Default,
            )
            IconButton(onClick = onBack, modifier = Modifier.align(Alignment.TopStart)) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.action_back),
                    tint = MaterialTheme.colorScheme.onSurface,
                )
            }
        }

        Column(
            modifier = Modifier
                .weight(SHEET_WEIGHT)
                .fillMaxWidth()
                .background(
                    MaterialTheme.colorScheme.background,
                    RoundedCornerShape(
                        topStart = MageRideTheme.radius.card,
                        topEnd = MageRideTheme.radius.card,
                    ),
                )
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            JourneySummary(
                pickup = state.draft.pickup?.address,
                dropoff = state.draft.dropoff?.address,
                onEdit = onEditRoute,
            )

            Toggles(
                state = state,
                onBookingFor = { value ->
                    model.setBookingFor(value)
                    if (value == BookingFor.SOMEONE_ELSE) onProxyDetails()
                },
                onSubject = { value ->
                    model.setSubject(value)
                    if (value == BookingSubject.PACKAGE) onPackageBooking()
                },
            )

            PublicSection(state = state, onSelect = model::selectRoute)

            SectionLabel(text = stringResource(R.string.booking_private_section))
            when {
                state.tiersLoading -> Loading(stringResource(R.string.booking_estimating))

                state.tiers.isEmpty() -> MutedRow(stringResource(R.string.booking_no_tiers))

                else -> state.tiers.forEach { quote ->
                    TierRow(
                        quote = quote,
                        selected = (state.selection as? BookingSelection.Private)?.quote == quote,
                        onSelect = { model.selectTier(quote) },
                    )
                }
            }

            // "Payment chip … (private tiers only)". A bus is not paid for in this app, so the
            // chip is not merely disabled on a public selection — it is absent.
            if (!state.isPublicSelected) {
                PaymentChip(method = state.draft.paymentMethod, onChange = model::setPaymentMethod)
            }

            state.error?.let { InlineError(message = stringResource(it)) }

            BookingCta(
                state = state,
                onBook = model::book,
                onSchedule = onSchedule,
                onTrackRoute = onTrackRoute,
            )
        }
    }
}

/** The wireframe's `● Galle Face ✎ ◆ Nugegoda` row. */
@Composable
private fun JourneySummary(pickup: String?, dropoff: String?, onEdit: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onEdit),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = pickup ?: stringResource(R.string.search_current_location),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = dropoff ?: stringResource(R.string.booking_no_destination),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
        Icon(
            imageVector = Icons.Filled.Edit,
            contentDescription = stringResource(R.string.booking_edit_route),
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** The wireframe's `[For Me | Someone else] [Person | Package]`. */
@Composable
private fun Toggles(state: RideBookingState, onBookingFor: (BookingFor) -> Unit, onSubject: (BookingSubject) -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        ToggleChip(
            label = stringResource(R.string.booking_for_me),
            selected = state.draft.bookingFor == BookingFor.ME,
            onClick = { onBookingFor(BookingFor.ME) },
        )
        ToggleChip(
            label = stringResource(R.string.booking_for_someone),
            selected = state.draft.bookingFor == BookingFor.SOMEONE_ELSE,
            onClick = { onBookingFor(BookingFor.SOMEONE_ELSE) },
        )
        ToggleChip(
            label = stringResource(R.string.booking_person),
            selected = state.draft.subject == BookingSubject.PERSON,
            onClick = { onSubject(BookingSubject.PERSON) },
        )
        ToggleChip(
            label = stringResource(R.string.booking_package),
            selected = state.draft.subject == BookingSubject.PACKAGE,
            onClick = { onSubject(BookingSubject.PACKAGE) },
        )
    }
}

@Composable
internal fun ToggleChip(label: String, selected: Boolean, onClick: () -> Unit) {
    AssistChip(
        onClick = onClick,
        label = { Text(label) },
        colors = androidx.compose.material3.AssistChipDefaults.assistChipColors(
            containerColor = if (selected) {
                MaterialTheme.colorScheme.primaryContainer
            } else {
                MaterialTheme.colorScheme.surface
            },
        ),
    )
}

/**
 * The one CTA, in whichever of its two forms applies.
 *
 * *"public → Track Route · private → Book Now / Schedule"* is D2' §SCR-PA-009's own sketch. There is
 * no Schedule under a public route because there is nothing to schedule: a bus runs to its own
 * timetable and this app does not book a seat on it.
 */
@Composable
private fun BookingCta(state: RideBookingState, onBook: () -> Unit, onSchedule: () -> Unit, onTrackRoute: () -> Unit) {
    if (state.isPublicSelected) {
        MageRideCta(label = stringResource(R.string.booking_track_route), onClick = onTrackRoute)
        return
    }

    MageRideCta(
        label = stringResource(R.string.booking_book_now),
        onClick = onBook,
        enabled = state.canBook,
        loading = state.booking,
    )
    OutlinedButton(onClick = onSchedule, modifier = Modifier.fillMaxWidth()) {
        Text(stringResource(R.string.booking_schedule))
    }
}

/** The map is the backdrop and the sheet is the work — the wireframe's own proportions. */
private const val MAP_WEIGHT = 0.4f
private const val SHEET_WEIGHT = 0.6f
