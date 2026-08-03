package lk.mageride.driver.delivery

import androidx.annotation.StringRes
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.asPoint
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.domain.geo.distanceMetres
import lk.mageride.shared.domain.ride.PackageGate
import lk.mageride.shared.domain.ride.PackageGateOutcome
import lk.mageride.shared.domain.ride.PackageHandoff
import lk.mageride.shared.domain.ride.PackageHandoffState

/**
 * Which of AL-33's three sheets is up.
 *
 * They are **sequential, not switchable**: a delivery moves forward through them and never back,
 * because each step is a thing that has already happened to the parcel.
 */
internal enum class DeliverySheet {

    /** SCR-DA-016a — the job in front of the driver, before they set off. */
    REVIEW,

    /** SCR-DA-016b — at the sender's door, entering the pickup code. */
    PICKUP,

    /** SCR-DA-016c — at the recipient's door, entering the delivery code or photographing it. */
    COMPLETE,
}

/** Which end of the delivery a call button reaches. */
internal enum class DeliveryParty {

    /** The account that booked the delivery, and the person holding the parcel at the start. */
    SENDER,

    /** The person the parcel is for. */
    RECIPIENT,
}

/**
 * SCR-DA-016a/b/c's state.
 *
 * @property ride The aggregate, as ride-svc last answered it.
 * @property position The driver's own last fix — the map marker, the SOS coordinate, and the
 *   `captured_geo` stamped on a proof photo.
 * @property gates `:shared`'s [PackageHandoff] state: the two OTP budgets and the proof flag.
 * @property started The driver tapped **Start delivery** on sheet 1. Local, because nothing on the
 *   wire corresponds to it — see [DeliveryViewModel.advance].
 * @property otp What has been typed into the sheet's four boxes.
 * @property proof A photograph queued against this delivery (P-10), if one has been taken.
 * @property dialNumber A number the screen should hand to the platform dialler, once.
 * @property finished The delivery is off this driver's hands — handed over, or released.
 * @property busy A command is in flight.
 * @property error Resolved copy for the last failure.
 */
internal data class DeliveryState(
    val ride: RideDetail? = null,
    val position: Fix? = null,
    val gates: PackageHandoffState = PackageHandoffState(),
    val started: Boolean = false,
    val otp: String = "",
    val proof: ProofUpload? = null,
    val dialNumber: String? = null,
    val finished: Boolean = false,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** Where the ride is, or `null` before the first read. */
    val rideState: RideState? get() = ride?.state

    /**
     * Whether the parcel is aboard.
     *
     * `package.picked_up` is the `→ InProgress` move (P-07), so the ride's own state is the answer
     * — not the local [started] flag, which only says the driver has set off.
     */
    val pickedUp: Boolean get() = rideState == RideState.InProgress || handedOver

    /**
     * Whether the parcel has been handed over.
     *
     * `Completed` onward, and **not** `isTerminal`: AL-33 decouples the cash from the handover, so
     * a COD delivery sits at `PaymentPending` with nothing left for the driver to do. Waiting for a
     * terminal ride state would hold a courier on a doorstep for a reconciliation that happens
     * somewhere else entirely (P-14).
     */
    val handedOver: Boolean
        get() = rideState == RideState.Completed ||
            rideState == RideState.PaymentPending ||
            rideState?.isTerminal == true

    /** Which sheet the delivery has reached. */
    val sheet: DeliverySheet
        get() = when {
            pickedUp -> DeliverySheet.COMPLETE
            started -> DeliverySheet.PICKUP
            else -> DeliverySheet.REVIEW
        }

    /** The OTP gate this sheet is asking about, or `null` on the review sheet. */
    val gate: PackageGate?
        get() = when (sheet) {
            DeliverySheet.REVIEW -> null
            DeliverySheet.PICKUP -> PackageGate.PICKUP
            DeliverySheet.COMPLETE -> PackageGate.DELIVERY
        }

    /** What the current gate's sheet should render (P-07). */
    val outcome: PackageGateOutcome? get() = gate?.let(gates::outcomeOf)

    /**
     * Whether five attempts are spent and the handoff is with the admin queue.
     *
     * The server's `423 otp-locked` is what sets it; the local count is a mirror that a second
     * device would not have, which is exactly why `PackageGateState` keeps the two apart.
     */
    val locked: Boolean get() = outcome is PackageGateOutcome.AdminQueue

    /** Attempts left on the current gate, for the counter under the boxes. */
    val attemptsRemaining: Int get() = gate?.let { gates.stateOf(it).attemptsRemaining } ?: MAX_ATTEMPTS

    /** Rejected attempts on the current gate — the wireframe's *"attempt N of 5"*. */
    val attemptsUsed: Int get() = gate?.let { gates.stateOf(it).attemptsUsed } ?: 0

    /** Whether the sheet's forward action can be tapped. */
    val canAdvance: Boolean
        get() = !busy && when (sheet) {
            DeliverySheet.REVIEW -> ride != null

            DeliverySheet.PICKUP -> !locked && PackageHandoff.isWellFormed(otp)

            // P-10: a photograph completes the delivery on its own when nobody is there to read
            // the code out, so sheet 3's CTA is live with either proof in hand.
            DeliverySheet.COMPLETE -> proof != null || (!locked && PackageHandoff.isWellFormed(otp))
        }

    /** How far the driver is from the pickup — the wireframe's `Pickup · 1.2 km`. */
    val pickupMetres: Double?
        get() = position?.let { fix -> ride?.pickup?.let { distanceMetres(fix.asPoint(), it.point) } }

    /**
     * How far the parcel has to travel — the wireframe's `Drop · 4.6 km`.
     *
     * Pickup to drop, not driver to drop: it is the length of the delivery, which is what a driver
     * deciding whether to take the job is reading.
     */
    val dropMetres: Double?
        get() = ride?.let { distanceMetres(it.pickup.point, it.dropoff.point) }

    /** MAP-10's 100 m circle — the pickup until the parcel is aboard, the drop afterwards. */
    val geofence: GeoPoint?
        get() = when {
            ride == null -> null
            pickedUp -> ride.dropoff.point
            else -> ride.pickup.point
        }

    /** The number behind a call button, or `null` when the ride does not carry it. */
    fun phoneOf(party: DeliveryParty): String? = when (party) {
        DeliveryParty.SENDER -> ride?.senderPhone

        // `counterpartyPhone` is the recipient on a package ride (Δ C037), and is the fallback for
        // a server that answers the older shape.
        DeliveryParty.RECIPIENT -> ride?.recipientPhone ?: ride?.counterpartyPhone
    }

    private companion object {
        const val MAX_ATTEMPTS = PackageHandoff.MAX_OTP_ATTEMPTS
    }
}
