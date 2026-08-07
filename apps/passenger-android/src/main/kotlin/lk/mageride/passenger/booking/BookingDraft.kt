package lk.mageride.passenger.booking

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import lk.mageride.passenger.location.LastKnownFix
import lk.mageride.passenger.settings.PaymentPreference
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.ride.RideKind

/** SCR-PA-009's *"For Me / Someone else"* toggle (US-8.16). */
internal enum class BookingFor { ME, SOMEONE_ELSE }

/** SCR-PA-009's *"Person / Package"* toggle (US-20.1). */
internal enum class BookingSubject { PERSON, PACKAGE }

/**
 * Which field the next chosen place belongs to.
 *
 * SCR-PA-008 is **one** screen reached from five places — the home sheet, SCR-PA-009's edit row,
 * SCR-PA-010b's Search method, SCR-PA-012's two ends and SCR-PA-013's destination picker — and it
 * has no way of knowing which. Nav arguments would work for the coordinate coming *back* but not
 * for the intent going *out*, so the intent is parked on the draft: whoever opens the picker says
 * what they opened it for, and [BookingDraft.capture] puts the answer where it belongs.
 *
 * `null` means "a fresh booking" — the home sheet's *"Where to?"*, which begins a draft rather than
 * editing one.
 */
internal enum class CaptureTarget {
    BOOKING_DROPOFF,
    BOOKING_PICKUP,
    PROXY_PICKUP,
    PACKAGE_PICKUP,
    PACKAGE_DROPOFF,
    SCHEDULE_DROPOFF,
}

/**
 * One booking, as the passenger has filled it in so far.
 *
 * @property pickup Where it starts. Defaults to the passenger's fix and is editable everywhere.
 * @property dropoff Where it ends. SCR-PA-008 sets it; SCR-PA-013 makes it mandatory (AL-36).
 * @property vehicleType The Mode C tier picked on SCR-PA-009, or `null` while a public route is
 *   selected — a bus is not a tier and is never booked.
 * @property riderName Proxy only (P-01). Blank until SCR-PA-010b.
 * @property riderPhone Proxy only, E.164 with no `+`. Hashed at rest server-side (P-03).
 * @property packageDropoff SCR-PA-012 captures a drop-off of its own: a parcel's destination is
 *   the recipient's, not the sender's, and the recipient can be asked for it (Request).
 * @property paymentMethod The **settlement-time** rail (`fare.yaml`'s `PaymentMethod`), because
 *   that is what a passenger actually chooses and what SCR-PA-016 confirms. `ride.yaml`'s
 *   booking-time enum is narrower and has not caught up with AL-57/AL-59 — see
 *   `PaymentRails.bookingValueOf`. Δ C080.
 */
internal data class BookingDraftState(
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val bookingFor: BookingFor = BookingFor.ME,
    val subject: BookingSubject = BookingSubject.PERSON,
    val vehicleType: RideVehicleType? = null,
    val paymentMethod: PaymentMethod = PaymentMethod.CASH,
    val riderName: String = "",
    val riderPhone: String = "",
    val packageSize: PackageSize = PackageSize.S,
    val packageDescription: String = "",
    val recipientName: String = "",
    val recipientPhone: String = "",
    val packagePickup: Place? = null,
    val packageDropoff: Place? = null,
) {

    /**
     * What `POST /v1/rides/request` is being asked for.
     *
     * Derived rather than stored, because the two toggles are what a passenger actually touches
     * and a third field holding their product would be a third thing to keep in step. **Package
     * wins over proxy**: `RideKind` has no `proxy_package` and the contract's own ordering makes
     * a parcel a package however it was booked — the rider fields still travel, so nothing about
     * who arranged it is lost.
     */
    val kind: RideKind
        get() = when {
            subject == BookingSubject.PACKAGE -> RideKind.PACKAGE
            bookingFor == BookingFor.SOMEONE_ELSE -> RideKind.PROXY
            else -> RideKind.PASSENGER
        }

    /** Whether SCR-PA-009 can quote at all — a fare needs both ends. */
    val isQuotable: Boolean get() = pickup != null && dropoff != null

    /** AL-36's gate on SCR-PA-013: Confirm stays disabled until a destination is set. */
    val hasDestination: Boolean get() = dropoff != null

    /**
     * Whether SCR-PA-010b has enough to book a proxy ride.
     *
     * A name and a number, because the driver's offer carries both (P-05) and the rider is called
     * on the number — an unnamed rider is a driver arriving at a stranger.
     */
    val isProxyComplete: Boolean
        get() = riderName.isNotBlank() && riderPhone.isNotBlank()

    /** Whether SCR-PA-012 has enough to quote a parcel. */
    val isPackageComplete: Boolean
        get() = packageDescription.isNotBlank() &&
            recipientName.isNotBlank() &&
            recipientPhone.isNotBlank() &&
            packagePickup != null &&
            packageDropoff != null
}

/**
 * The booking being assembled, shared by every screen in cluster 3.
 *
 * **A `single`, and deliberately so.** SCR-PA-008 chooses a destination, SCR-PA-009 picks a tier,
 * SCR-PA-010b adds a rider, SCR-PA-012 adds a parcel, SCR-PA-013 adds a time and C080's
 * SCR-PA-016 changes the payment method — six screens editing **one** booking. The alternatives
 * are both worse: nav arguments would serialise a rider's phone number and a package description
 * into a URL in the back stack, and a view model per screen with no shared home would make
 * "go back and change the pickup" lose everything downstream of it.
 *
 * Its lifetime is the process's, which is why [clear] exists and is called the moment a booking
 * becomes a ride. A draft left behind is the next booking's stale rider name.
 *
 * **Every booking starts on the passenger's default payment method** (US-22.4, Δ C083). SCR-PA-027
 * stores the rail and this is what *"pre-selected at booking/checkout"* means in code: a fresh
 * draft — a new one, a [begin] and a [clear] alike — opens on [PaymentPreference.current], and the
 * chip on SCR-PA-009 is drawing that. Changing it there still changes only this booking, because a
 * preference is not a command.
 *
 * **Every booking also starts from where the passenger is** (Δ C097). [begin] falls back to
 * [lastFix] when it is given no pickup, and that is a **defect fix rather than a convenience**: all
 * three production `begin(…)` call sites omitted the argument, so `pickup` was null, and
 * `RideBookingViewModel.refresh()` returns early on exactly that — SCR-PA-009 showed no routes and
 * no tiers at all. Defaulting here rather than at the call sites is what stops a fourth one
 * reintroducing it. See [LastKnownFix].
 *
 * @property payments The stored default. Read on every fresh draft rather than captured once, so a
 *   change made in Settings applies to the **next** booking without anything having to be told.
 * @property lastFix Where the passenger was when the map last saw them. Read on every fresh draft
 *   for the same reason [payments] is.
 */
internal class BookingDraft(private val payments: PaymentPreference, private val lastFix: LastKnownFix) {

    private val mutableState = MutableStateFlow(BookingDraftState(paymentMethod = payments.current))

    val state: StateFlow<BookingDraftState> = mutableState.asStateFlow()

    /** The draft right now, for a caller that needs a value rather than a stream. */
    val current: BookingDraftState get() = mutableState.value

    /**
     * What the place picker was opened for, or `null` for a fresh booking.
     *
     * Plain state rather than a flow: nothing renders it, and it lives for exactly as long as one
     * navigation to SCR-PA-008 and back.
     */
    var pendingCapture: CaptureTarget? = null
        private set

    /** A screen is about to open the picker. */
    fun expect(target: CaptureTarget) {
        pendingCapture = target
    }

    /**
     * The picker answered. Returns whether the place was consumed by a pending capture.
     *
     * `false` means nobody was waiting for it, which is the *"Where to?"* case — the caller starts
     * a new booking with [begin] instead.
     */
    fun capture(place: Place): Boolean {
        val target = pendingCapture ?: return false
        pendingCapture = null
        update { draft ->
            when (target) {
                CaptureTarget.BOOKING_DROPOFF, CaptureTarget.SCHEDULE_DROPOFF -> draft.copy(dropoff = place)
                CaptureTarget.BOOKING_PICKUP -> draft.copy(pickup = place)
                CaptureTarget.PROXY_PICKUP -> draft.copy(pickup = place)
                CaptureTarget.PACKAGE_PICKUP -> draft.copy(packagePickup = place)
                CaptureTarget.PACKAGE_DROPOFF -> draft.copy(packageDropoff = place)
            }
        }
        return true
    }

    /** Applies [change]. Every screen edits through here so there is one writer. */
    fun update(change: (BookingDraftState) -> BookingDraftState) {
        mutableState.update(change)
    }

    /**
     * Starts a fresh booking from a chosen destination.
     *
     * Called by SCR-PA-008 rather than by SCR-PA-009, because choosing a destination is what
     * *begins* a booking — arriving at the booking screen with the previous attempt's rider still
     * attached is the bug this prevents.
     *
     * @param pickup Where it starts. **Defaults to the last known fix**, because a booking with no
     *   pickup cannot be quoted at all — see the class KDoc and [LastKnownFix]. A caller with a
     *   better answer (a proxy rider's shared pin, a package's own end) passes one.
     */
    fun begin(dropoff: Place, pickup: Place? = null) {
        pendingCapture = null
        mutableState.value = BookingDraftState(
            dropoff = dropoff,
            pickup = pickup ?: lastFix.asPlace(),
            paymentMethod = payments.current,
        )
    }

    /** Throws the draft away — after a ride is requested, or when the passenger leaves the flow. */
    fun clear() {
        pendingCapture = null
        mutableState.value = BookingDraftState(paymentMethod = payments.current)
    }
}
