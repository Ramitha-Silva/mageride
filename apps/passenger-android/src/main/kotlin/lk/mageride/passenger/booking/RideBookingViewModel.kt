package lk.mageride.passenger.booking

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.map.EncodedPolyline
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.transit.TransitCoverage
import lk.mageride.shared.data.models.transit.TransitOption
import lk.mageride.shared.data.models.transit.TransitOptionKind
import lk.mageride.shared.domain.geo.distanceMetres

/**
 * One Mode C tier, as SCR-PA-009 is allowed to show it.
 *
 * **There is no `etaSeconds` and no `distanceMetres` on this class, and that is AL-19 expressed as
 * a type.** D5' §BR-23.3: *"Before dispatch, Mode C private tiers expose the upfront price only —
 * 'minutes away' and 'distance to driver' are suppressed (no driver matched yet)."* A tier card
 * cannot render a number it was never given, so the fence holds even if somebody later adds a row
 * to the layout.
 *
 * @property token The `fareEstimateToken` this price is bound to. `POST /v1/rides/request` rejects
 *   a booking whose token does not match its quote (`400 invalid-fare-token`), which is what stops
 *   a client naming its own fare.
 */
internal data class TierQuote(val vehicleType: RideVehicleType, val amountMinor: Long, val token: String)

/** A GTFS option as the list draws it — route number, description, and the Direct/Transit tag. */
internal data class PublicRouteRow(
    val routeId: String,
    val routeShortName: String,
    val headsign: String?,
    val description: String?,
    val kind: TransitOptionKind,
    val transfers: Int,
    val walkingDistanceM: Int?,
)

/** Which of the two lists the passenger has picked from. Exactly one, or neither. */
internal sealed interface BookingSelection {
    data class Public(val route: PublicRouteRow) : BookingSelection
    data class Private(val quote: TierQuote) : BookingSelection
}

/**
 * SCR-PA-009's state.
 *
 * @property routes Direct options first, then transfer ones (BR-23.2's ordering, server-side).
 * @property coverage Whether transit-svc could answer. [TransitCoverage.NO_FEED] hides the whole
 *   public section behind a muted row rather than failing the screen (AL-55).
 * @property routesFailed transit-svc was unreachable. Renders as the same muted row: a passenger
 *   does not care which of the two happened, and the private tiers work either way.
 * @property routePolyline The selected route's GTFS shape, decoded. Empty for a private tier.
 * @property walkPolyline The blue line to the nearest halt, drawn only when the passenger is not
 *   already on the route.
 * @property walkHalt Which halt that line ends at, and how far — the *"Walk 250 m to Pamankada
 *   halt"* hint. `null` when they are on-route.
 * @property booked The ride this screen created, once it has. The screen navigates on it.
 */
internal data class RideBookingState(
    val draft: BookingDraftState = BookingDraftState(),
    val routes: List<PublicRouteRow> = emptyList(),
    val coverage: TransitCoverage = TransitCoverage.ACTIVE,
    val routesFailed: Boolean = false,
    val routesLoading: Boolean = false,
    val tiers: List<TierQuote> = emptyList(),
    val tiersLoading: Boolean = false,
    val selection: BookingSelection? = null,
    val routePolyline: List<GeoPoint> = emptyList(),
    val walkPolyline: List<GeoPoint> = emptyList(),
    val walkHalt: WalkHint? = null,
    val booking: Boolean = false,
    val booked: String? = null,
    @param:StringRes val error: Int? = null,
) {
    /** Whether the public section is replaced by *"Bus route info coming soon for this area"*. */
    val publicUnavailable: Boolean
        get() = !routesLoading && (routesFailed || coverage == TransitCoverage.NO_FEED)

    /** A public route is tracked, never booked — no fare, no payment chip, no `POST /rides`. */
    val isPublicSelected: Boolean get() = selection is BookingSelection.Public

    /** Whether Book Now can fire. */
    val canBook: Boolean
        get() = !booking && selection is BookingSelection.Private && draft.isQuotable
}

/** The *"Walk 250 m to Pamankada halt"* hint under a selected public route. */
internal data class WalkHint(val haltName: String, val metres: Int)

/**
 * SCR-PA-009 — the multimodal list, and the booking.
 *
 * **Two fences, and they are the reason this class exists rather than a screen reading two APIs.**
 *
 * 1. **AL-19 / BR-23.3.** A Mode C tier shows the upfront price and nothing else before a driver
 *    is matched. [TierQuote] carries no ETA and no distance, so the card physically cannot.
 * 2. **AL-18 / BR-23.2.** Mode A options are the GTFS feed's — route number, headsign, Direct or
 *    Transit — and they are *tracked*, not booked. Selecting one clears the tier, empties the
 *    payment decision and changes the CTA; there is no fare on a bus.
 *
 * **The two lists load independently and neither blocks the other.** transit-svc being down or
 * having no feed for a corridor (AL-55) hides the public section behind a muted row while the
 * private tiers quote normally — *"nothing blocks on GTFS coverage"* is D2' §SCR-PA-009's own
 * wording. The reverse holds too: a fare-svc outage leaves the bus routes usable.
 */
@Suppress("TooManyFunctions") // Two independent lists, four controls and a booking on one screen.
internal class RideBookingViewModel(
    private val draft: BookingDraft,
    private val bookings: BookingRepository,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(RideBookingState(draft = draft.current))

    val state: StateFlow<RideBookingState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch {
            draft.state.collect { latest -> mutableState.update { it.copy(draft = latest) } }
        }
        refresh()
    }

    /** Re-reads both lists. Called on entry and whenever an end of the journey moves. */
    fun refresh() {
        val current = draft.current
        val from = current.pickup?.point ?: return
        val to = current.dropoff?.point ?: return

        viewModelScope.launch { loadRoutes(from, to) }
        viewModelScope.launch { loadTiers(from, to, current) }
    }

    /** SCR-PA-009's *"For Me / Someone else"*. The screen navigates to SCR-PA-010b on the second. */
    fun setBookingFor(value: BookingFor) {
        draft.update { it.copy(bookingFor = value) }
    }

    /** SCR-PA-009's *"Person / Package"*. A package re-quotes: the tiers and the kind both change. */
    fun setSubject(value: BookingSubject) {
        draft.update { it.copy(subject = value, vehicleType = null) }
        mutableState.update { it.copy(selection = null, tiers = emptyList()) }
        refresh()
    }

    /** The payment chip. Private tiers only — a bus is not paid for in this app. */
    fun setPaymentMethod(method: RidePaymentMethod) {
        draft.update { it.copy(paymentMethod = method) }
    }

    /**
     * A tier was chosen.
     *
     * Clears the route line as well as the selection: a map showing a bus route under a *"Book
     * Now"* for a tuk-tuk is two answers to one question.
     */
    fun selectTier(quote: TierQuote) {
        draft.update { it.copy(vehicleType = quote.vehicleType) }
        mutableState.update {
            it.copy(
                selection = BookingSelection.Private(quote),
                routePolyline = emptyList(),
                walkPolyline = emptyList(),
                walkHalt = null,
                error = null,
            )
        }
    }

    /**
     * A public route was chosen — draw it.
     *
     * The CTA becomes *"Track Route"*, the tier is dropped from the draft and no fare is quoted:
     * D2' §SCR-PA-009 is explicit that *"no fare/payment is charged (public transport)"*.
     */
    fun selectRoute(route: PublicRouteRow) {
        draft.update { it.copy(vehicleType = null) }
        mutableState.update { it.copy(selection = BookingSelection.Public(route), error = null) }
        viewModelScope.launch { drawRoute(route) }
    }

    /**
     * Book Now — `POST /v1/rides/request`.
     *
     * **The `clientRequestId` is minted once per attempt and reused as the idempotency key.** R-18
     * dedupes on `(passengerId, clientRequestId)`, so a retry after a timeout returns the ride the
     * first call created rather than booking a second one; a fresh id per retry would be the bug
     * that rule exists to prevent.
     */
    @Suppress("TooGenericExceptionCaught", "ReturnCount") // Four preconditions, each its own guard.
    fun book() {
        val current = mutableState.value
        val quote = (current.selection as? BookingSelection.Private)?.quote ?: return
        val pickup = current.draft.pickup ?: return
        val dropoff = current.draft.dropoff ?: return
        if (current.booking) return

        mutableState.update { it.copy(booking = true, error = null) }
        viewModelScope.launch {
            try {
                val response = bookings.requestRide(
                    RideRequest(
                        clientRequestId = keys.next(),
                        kind = current.draft.kind,
                        pickup = pickup,
                        dropoff = dropoff,
                        vehicleType = quote.vehicleType,
                        fareEstimateToken = quote.token,
                        paymentMethod = current.draft.paymentMethod,
                        isProxy = current.draft.kind == RideKind.PROXY,
                        riderName = current.draft.riderName.takeIf { it.isNotBlank() },
                        riderPhone = current.draft.riderPhone.takeIf { it.isNotBlank() },
                    ),
                )
                // The booking is a ride now; the draft has done its job and must not survive into
                // the next one.
                draft.clear()
                mutableState.update { it.copy(booking = false, booked = response.rideId) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(booking = false, error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    /** The screen has navigated away from the created ride. */
    fun onBookingConsumed() {
        mutableState.update { it.copy(booked = null) }
    }

    /** Dismisses an inline failure. */
    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    // ------------------------------------------------------------------------------------------

    /**
     * The GTFS half.
     *
     * A failure is **not** an error state: AL-55 and D2' §SCR-PA-009 both say the private tiers and
     * the live map keep working when route matching cannot answer, so this sets a flag the list
     * renders as one muted row and nothing else changes.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadRoutes(from: GeoPoint, to: GeoPoint) {
        mutableState.update { it.copy(routesLoading = true, routesFailed = false) }
        try {
            val answer = bookings.transitOptions(from, to)
            mutableState.update {
                it.copy(
                    routes = answer.options.map(TransitOption::asRow),
                    coverage = answer.coverage,
                    routesLoading = false,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            mutableState.update { it.copy(routesLoading = false, routesFailed = true, routes = emptyList()) }
        }
    }

    /**
     * The Mode C half — one `GET /v1/fare/estimate` per tier, concurrently.
     *
     * One call per tier because that is what the contract offers: `estimateFare` takes a single
     * `vehicleType` and answers a single token, and a token is what a booking must carry. Run in
     * parallel because six sequential round trips on a 3G connection is the difference between a
     * screen that fills and one that crawls.
     *
     * A tier whose estimate failed is **left out** rather than shown priceless. A card with no
     * price is a card a passenger will tap.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadTiers(from: GeoPoint, to: GeoPoint, current: BookingDraftState) {
        mutableState.update { it.copy(tiersLoading = true) }
        val kind = if (current.kind == RideKind.PACKAGE) FareEstimateKind.PACKAGE else FareEstimateKind.PASSENGER
        val types = tiersFor(current)

        val quotes = coroutineScope {
            types.map { type ->
                async {
                    try {
                        val quote = bookings.estimate(from, to, type, kind)
                        TierQuote(vehicleType = type, amountMinor = quote.amountMinor, token = quote.fareEstimateToken)
                    } catch (cause: CancellationException) {
                        throw cause
                    } catch (_: Throwable) {
                        null
                    }
                }
            }.awaitAll()
        }.filterNotNull()

        mutableState.update { it.copy(tiers = quotes, tiersLoading = false) }
    }

    /** Draws the selected route, and the walk to it when the passenger is not already on it. */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun drawRoute(route: PublicRouteRow) {
        val from = draft.current.pickup?.point ?: return
        try {
            val detail = bookings.transitRoute(route.routeId, around = from)
            val shape = EncodedPolyline.decode(detail.shape)
            val halt = (detail.nearestStops ?: detail.stops).minByOrNull { distanceMetres(from, it.point) }
            val metres = halt?.let { distanceMetres(from, it.point).toInt() }

            mutableState.update {
                it.copy(
                    routePolyline = shape,
                    // "if off-route a blue walking polyline routes to the closest halt" — and if
                    // they are already at one, no line and no hint. On-route is the common case
                    // and drawing a two-metre line for it would be visual noise.
                    walkPolyline = if (halt != null && metres != null && metres > ON_ROUTE_METRES) {
                        listOf(from, halt.point)
                    } else {
                        emptyList()
                    },
                    walkHalt = if (halt != null && metres != null && metres > ON_ROUTE_METRES) {
                        WalkHint(haltName = halt.name, metres = metres)
                    } else {
                        null
                    },
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // The row stays selected and the CTA still tracks; there is simply no line to draw.
            mutableState.update { it.copy(routePolyline = emptyList(), walkPolyline = emptyList(), walkHalt = null) }
        }
    }

    /**
     * Which tiers to quote.
     *
     * A person gets the six passenger types; a parcel gets the ones its size fits in, which is
     * P-06's own hint made operational — offering a motorbike for a fridge is a driver arriving
     * at a job they cannot do. `truck`/`mini_truck` are delivery-only (AL-09) and appear here and
     * nowhere else in the app.
     */
    private fun tiersFor(current: BookingDraftState): List<RideVehicleType> = if (current.kind == RideKind.PACKAGE) {
        when (current.packageSize) {
            PackageSize.S -> listOf(RideVehicleType.MOTORBIKE, RideVehicleType.THREE_WHEELER)
            PackageSize.M -> listOf(RideVehicleType.THREE_WHEELER, RideVehicleType.FLEX, RideVehicleType.SEDAN)
            PackageSize.L -> listOf(RideVehicleType.VAN, RideVehicleType.MINI_TRUCK, RideVehicleType.TRUCK)
        }
    } else {
        PASSENGER_TIERS
    }

    private companion object {

        /** AL-09's bookable passenger types, cheapest-first as the wireframe lists them. */
        val PASSENGER_TIERS = listOf(
            RideVehicleType.MOTORBIKE,
            RideVehicleType.THREE_WHEELER,
            RideVehicleType.FLEX,
            RideVehicleType.SEDAN,
            RideVehicleType.MINI_VAN,
            RideVehicleType.VAN,
        )

        /**
         * Close enough to a halt to count as being at it.
         *
         * BR-23.2's halt radius is an admin setting defaulting to 400 m — the distance at which
         * transit-svc considers a stop to serve a point at all. Using the same figure here means
         * "on-route" on this screen and "direct" on the server agree about the same passenger.
         */
        const val ON_ROUTE_METRES = 400
    }
}

/**
 * A transit option as one row.
 *
 * The **first** leg is what the row is named after, because that is the vehicle the passenger
 * boards; a transfer's second route is described by the transfer count, which is what the wireframe
 * draws (*"1 transfer at Nugegoda"*).
 */
private fun TransitOption.asRow(): PublicRouteRow {
    val first = legs.firstOrNull()
    return PublicRouteRow(
        routeId = first?.routeId.orEmpty(),
        routeShortName = first?.routeShortName.orEmpty(),
        headsign = first?.headsign,
        description = first?.description,
        kind = kind,
        transfers = (legs.size - 1).coerceAtLeast(0),
        walkingDistanceM = walkingDistanceM,
    )
}
