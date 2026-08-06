package lk.mageride.passenger.booking

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.R
import lk.mageride.passenger.onboarding.PhoneNumber
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest

/**
 * Which end of the parcel's journey a capture control is filling in.
 *
 * The two are **not** symmetric, and the fence says so: pickup offers Search / Map / Paste link,
 * drop-off offers those plus **Request**. That is not an oversight — the sender is standing at the
 * pickup, so there is nobody to ask; the recipient is the only person who knows where the parcel
 * is going, and asking them is what the fourth method is for.
 */
internal enum class PackageEnd { PICKUP, DROPOFF }

/**
 * SCR-PA-012's state.
 *
 * @property size P-06's S/M/L. Drives [sizeHint] and, on SCR-PA-009, which tiers are quoted.
 * @property estimateMinor The quote, once *"Get estimate & Book"* has fetched one.
 * @property quoteToken The `fareEstimateToken` that price is bound to. Booking without it is
 *   `400 invalid-fare-token`, which is exactly what stops a client naming its own delivery fee.
 * @property pickupOtp **Shown once, never returned again** (P-07). The server keeps only its hash.
 */
internal data class PackageBookingState(
    val size: PackageSize = PackageSize.S,
    val description: String = "",
    val recipientName: String = "",
    val recipientPhone: String = "",
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val pickupMethod: PickupMethod = PickupMethod.SEARCH,
    val dropoffMethod: PickupMethod = PickupMethod.SEARCH,
    val paymentMethod: RidePaymentMethod = RidePaymentMethod.CASH,
    val estimating: Boolean = false,
    val estimateMinor: Long? = null,
    val quoteToken: String? = null,
    val booking: Boolean = false,
    val booked: String? = null,
    val pickupOtp: String? = null,
    @param:StringRes val error: Int? = null,
) {

    /** P-06's per-size hint, which *"updates per pick"* — the wireframe's ⓘ line. */
    @get:StringRes
    val sizeHint: Int
        get() = when (size) {
            PackageSize.S -> R.string.package_hint_s
            PackageSize.M -> R.string.package_hint_m
            PackageSize.L -> R.string.package_hint_l
        }

    /** Whether there is enough to ask fare-svc for a price. */
    val canEstimate: Boolean
        get() = !estimating && !booking &&
            description.isNotBlank() &&
            recipientName.isNotBlank() &&
            PhoneNumber.isValid(recipientPhone) &&
            pickup != null &&
            dropoff != null

    /** Whether Book can fire — an estimate has to exist first, because a token binds the price. */
    val canBook: Boolean get() = canEstimate && estimateMinor != null
}

/**
 * SCR-PA-012 — a parcel instead of a person.
 *
 * **Same fare as a Mode C ride** (US-20.9), so this books through exactly the same
 * `POST /v1/rides/request` with `kind = package` and a `FareEstimateKind.PACKAGE` quote. What is
 * genuinely different is the *cargo*: a size, a description, a recipient who is not the booker, and
 * two ends that are both somebody else's.
 *
 * **The pickup OTP is shown once and never again** (P-07). `RequestRideResponse.pickupOtp` is
 * present only on a package booking and only in that one response — the server stores its hash —
 * so the state keeps it and the screen must surface it before the passenger leaves.
 *
 * **COD is a booking-time method and a package-only one** (AL-22, US-20.8). `RidePaymentMethod` has
 * it; a passenger ride offering it would be `400 payment-method-invalid`, which is why the payment
 * list on this screen is not the same list as SCR-PA-009's.
 */
@Suppress("TooManyFunctions") // One setter per field on the wireframe's longest form.
internal class PackageBookingViewModel(
    private val draft: BookingDraft,
    private val bookings: BookingRepository,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(
        PackageBookingState(
            size = draft.current.packageSize,
            description = draft.current.packageDescription,
            recipientName = draft.current.recipientName,
            recipientPhone = draft.current.recipientPhone,
            pickup = draft.current.packagePickup ?: draft.current.pickup,
            dropoff = draft.current.packageDropoff ?: draft.current.dropoff,
        ),
    )

    val state: StateFlow<PackageBookingState> = mutableState.asStateFlow()

    /** P-06's selector. Changing it invalidates the quote — a bigger parcel is a bigger vehicle. */
    fun setSize(size: PackageSize) {
        mutableState.update { it.copy(size = size, estimateMinor = null, quoteToken = null) }
        draft.update { it.copy(packageSize = size, subject = BookingSubject.PACKAGE) }
    }

    fun onDescriptionChanged(value: String) {
        mutableState.update { it.copy(description = value) }
        draft.update { it.copy(packageDescription = value) }
    }

    fun onRecipientNameChanged(value: String) {
        mutableState.update { it.copy(recipientName = value) }
        draft.update { it.copy(recipientName = value) }
    }

    fun onRecipientPhoneChanged(value: String) {
        val normalised = PhoneNumber.normalise(value)
        mutableState.update { it.copy(recipientPhone = normalised) }
        draft.update { it.copy(recipientPhone = normalised) }
    }

    /** The Search / Map / Paste link (/ Request, drop-off only) control for one end. */
    fun setMethod(end: PackageEnd, method: PickupMethod) {
        // The fence, enforced rather than only drawn: there is nobody at the pickup to ask.
        if (end == PackageEnd.PICKUP && method == PickupMethod.REQUEST) return
        mutableState.update {
            if (end == PackageEnd.PICKUP) it.copy(pickupMethod = method) else it.copy(dropoffMethod = method)
        }
    }

    /** Whatever the chosen method produced for one end. Moving either end invalidates the quote. */
    fun setPlace(end: PackageEnd, place: Place) {
        mutableState.update {
            if (end == PackageEnd.PICKUP) {
                it.copy(pickup = place, estimateMinor = null, quoteToken = null)
            } else {
                it.copy(dropoff = place, estimateMinor = null, quoteToken = null)
            }
        }
        draft.update {
            if (end == PackageEnd.PICKUP) it.copy(packagePickup = place) else it.copy(packageDropoff = place)
        }
    }

    /** Cash / LankaQR / OnePay / **COD** — the last of which exists only here (US-20.8). */
    fun setPaymentMethod(method: RidePaymentMethod) {
        mutableState.update { it.copy(paymentMethod = method) }
        draft.update { it.copy(paymentMethod = method) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * *"Get estimate & Book"* — the estimate half.
     *
     * A parcel is quoted on the **smallest vehicle its size fits**, which is the cheapest honest
     * answer: P-06's hint has already told the sender that an L needs a van, so quoting them a van
     * is what they were led to expect.
     */
    @Suppress("TooGenericExceptionCaught", "ReturnCount") // Three preconditions, each its own guard.
    fun estimate() {
        val current = mutableState.value
        if (!current.canEstimate) return
        val pickup = current.pickup ?: return
        val dropoff = current.dropoff ?: return

        mutableState.update { it.copy(estimating = true, error = null) }
        viewModelScope.launch {
            try {
                val quote = bookings.estimate(
                    from = pickup.point,
                    to = dropoff.point,
                    vehicleType = vehicleFor(current.size),
                    kind = FareEstimateKind.PACKAGE,
                )
                mutableState.update {
                    it.copy(estimating = false, estimateMinor = quote.amountMinor, quoteToken = quote.fareEstimateToken)
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(estimating = false, error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * The booking — `POST /v1/rides/request` with `kind = package`.
     *
     * The response's `pickupOtp` is kept in state because it is **shown once and never returned
     * again** (P-07): the server holds only its hash, so a screen that navigated away without
     * surfacing it would have destroyed the only copy the sender will ever get.
     */
    @Suppress("TooGenericExceptionCaught", "ReturnCount") // Four preconditions, each its own guard.
    fun book() {
        val current = mutableState.value
        val token = current.quoteToken ?: return
        val pickup = current.pickup ?: return
        val dropoff = current.dropoff ?: return
        if (!current.canBook) return

        mutableState.update { it.copy(booking = true, error = null) }
        viewModelScope.launch {
            try {
                val response = bookings.requestRide(
                    RideRequest(
                        clientRequestId = keys.next(),
                        kind = RideKind.PACKAGE,
                        pickup = pickup,
                        dropoff = dropoff,
                        vehicleType = vehicleFor(current.size),
                        fareEstimateToken = token,
                        paymentMethod = current.paymentMethod,
                        packageSize = current.size,
                        packageDescription = current.description,
                        recipientName = current.recipientName,
                        recipientPhone = PhoneNumber.toE164(current.recipientPhone),
                    ),
                )
                draft.clear()
                mutableState.update {
                    it.copy(booking = false, booked = response.rideId, pickupOtp = response.pickupOtp)
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(booking = false, error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    /** The screen has shown the OTP and navigated on. */
    fun onBookingConsumed() {
        mutableState.update { it.copy(booked = null, pickupOtp = null) }
    }

    /**
     * The smallest vehicle P-06 says this size fits.
     *
     * S is a motorbike box, M a three-wheeler, L a van — the same three the size hint names, so the
     * price a sender is quoted matches the vehicle they were told it needs. `truck`/`mini_truck`
     * exist for freight beyond this screen (AL-09).
     */
    private fun vehicleFor(size: PackageSize): RideVehicleType = when (size) {
        PackageSize.S -> RideVehicleType.MOTORBIKE
        PackageSize.M -> RideVehicleType.THREE_WHEELER
        PackageSize.L -> RideVehicleType.VAN
    }
}
