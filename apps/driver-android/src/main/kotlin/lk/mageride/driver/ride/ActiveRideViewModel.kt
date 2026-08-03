package lk.mageride.driver.ride

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.asPoint
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.domain.ride.RideCommand
import lk.mageride.shared.domain.ride.RideProjection
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/** Which of SCR-DA-015's three sheets is up. At most one, which is why this is an enum. */
internal enum class RideSheet {

    /** The rider's four-digit start OTP; a package's sender OTP on the same entry (P-07). */
    START_OTP,

    /** AL-48's chooser — **Free call** (in-app VoIP) or **Normal call** (a direct cellular dial). */
    CALL_TYPE,

    /** AL-47's *"QR payment received?"* attestation (US-26.1). */
    QR_CONFIRM,
}

/**
 * SCR-DA-015's state.
 *
 * @property ride The aggregate, as ride-svc last answered it.
 * @property position The driver's own last fix — the map marker and the SOS coordinate.
 * @property sheet Which sheet is up, if any.
 * @property dialNumber A number the screen should hand to the platform dialler, once.
 * @property qrAttested Whether the driver has already answered AL-47's *"QR payment received?"*.
 * @property finished The ride has reached a state that takes the driver back to standby.
 * @property busy A command is in flight.
 * @property error Resolved copy for the last failure.
 */
internal data class ActiveRideState(
    val ride: RideDetail? = null,
    val position: Fix? = null,
    val sheet: RideSheet? = null,
    val dialNumber: String? = null,
    val qrAttested: Boolean = false,
    val finished: Boolean = false,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** Where the ride is, or `null` before the first read. */
    val rideState: RideState? get() = ride?.state

    /**
     * Whether this ride settles by **AL-47 attestation** rather than by a gateway callback.
     *
     * The passenger scanned the driver's own bank LankaQR, so the money moved bank-to-bank and
     * nothing outside the driver's banking app ever saw it. That is the whole reason the confirm
     * sheet exists: there is no webhook that could close this payment.
     */
    val settlesByDriverQr: Boolean get() = ride?.paymentMethod == RidePaymentMethod.LANKAQR

    /**
     * Whether the driver-QR confirm is the action the ride is now waiting on (AL-47, US-26.1).
     *
     * **[qrAttested] is what closes it, not the ride's state.** Confirming settles the *payment* —
     * `DriverConfirmedQR` is terminal on the payment machine — but the ride sits at
     * `PaymentPending` until fare-svc's `payment-settled` reaches ride-svc through the outbox and
     * moves it to `CashSettled`. Asking again in that window would put the sheet back up on a
     * driver who has already answered, and a second attestation is not a thing they can give.
     */
    val awaitingQrConfirm: Boolean
        get() = settlesByDriverQr &&
            !qrAttested &&
            rideState in setOf(RideState.Completed, RideState.PaymentPending)

    /**
     * Where MAP-10's 100 m circle goes — the point the driver is being sent to *now*.
     *
     * The pickup until the rider is on board and the drop after, which is the same circle D5' §7
     * evaluates `DriverArrived` against.
     */
    val geofence: GeoPoint?
        get() = when (rideState) {
            RideState.Accepted, RideState.DriverArrived -> ride?.pickup?.let { GeoPoint(it.lat, it.lng) }
            RideState.InProgress -> ride?.dropoff?.let { GeoPoint(it.lat, it.lng) }
            else -> null
        }
}

/**
 * Folds a server-confirmed state onto the screen.
 *
 * The two rules a `copy` would get wrong: a **driver-QR ride is not over at `PaymentPending`** —
 * AL-47's confirm is what settles it, so the sheet comes up instead of the screen closing — and
 * `finished` is the ride's own `isTerminal`, never a guess from which command was just sent.
 */
private fun ActiveRideState.advancedTo(moved: RideStateSnapshot): ActiveRideState {
    val next = copy(
        busy = false,
        ride = ride?.copy(state = moved.state, version = moved.version),
    )
    return next.copy(
        sheet = if (next.awaitingQrConfirm) RideSheet.QR_CONFIRM else next.sheet,
        // Sticky, because terminal is terminal: a ride that has finished never un-finishes, and a
        // late poll landing after the driver has been sent back to standby must not undo that.
        finished = next.finished || (moved.state.isTerminal && !next.awaitingQrConfirm),
    )
}

/**
 * **SCR-DA-015 · the active ride** — accepted through to payment.
 *
 * **The client never advances a ride.** `RideProjection` moves only on a server-confirmed state,
 * and `verdict(command)` is the other direction: a *local* guess about whether a command is legal
 * from here, used to decide which CTA the sheet draws. It is never a claim about what ride-svc
 * would allow — ride-svc is the sole writer (R-01) and its answer is applied whatever the table
 * says.
 *
 * **Every mutation echoes the version the screen was showing** (R-14). A `409 version-conflict`
 * means somebody else moved the ride, so the answer is to re-read and let the driver decide again
 * — never to bump the number and retry.
 *
 * **The state poll is the documented fallback, and here it is the only path.** D3' §3.1 puts ride
 * transitions on the SignalR hub with `GET /v1/rides/{rideId}/state` as the backplane-down fallback
 * (D6' §8.3). `:shared` carries the hub's contract (`LiveHub`) and **no client for it**, so this
 * screen polls. See the C070 handoff.
 */
// Eleven members, and the count is SCR-DA-015's rather than a design choice: the wireframe's own
// controls are the forward CTA (three states), the OTP entry, Call, SOS and the AL-47 confirm, and
// each is a distinct command with a distinct failure. Splitting them across two view models would
// need a rule to keep the ride's version in step between the halves — which is the one thing R-14
// makes it fatal to get wrong.
@Suppress("TooManyFunctions")
internal class ActiveRideViewModel(
    private val rideId: Ulid,
    private val rides: ActiveRideRepository,
    private val contact: RideContact,
    private val location: DriverLocationSource,
) : ViewModel() {

    private val mutableState = MutableStateFlow(ActiveRideState())

    val state: StateFlow<ActiveRideState> = mutableState.asStateFlow()

    private var projection: RideProjection? = null

    init {
        refresh()
        observeDevice()
    }

    /** Re-reads the whole aggregate and re-seats the projection on it. */
    fun refresh() {
        launchGuarded {
            val detail = rides.detail(rideId)
            projection = RideProjection.of(detail)
            mutableState.update {
                it.copy(ride = detail).advancedTo(RideStateSnapshot(detail.state, detail.version))
            }
        }
    }

    /**
     * The single forward CTA, whatever the ride's state calls it.
     *
     * `Accepted` → **I've arrived** · `DriverArrived` → **Start ride** (which raises the OTP entry
     * rather than sending anything) · `InProgress` → **Complete ride**. One entry point because the
     * wireframe draws one button, and because which command is legal is a property of the ride
     * rather than of the tap — [RideProjection.canSend] is what decides, from ADD Appendix B.2's
     * table.
     */
    fun advance() {
        val current = mutableState.value.ride ?: return
        when (current.state) {
            RideState.Accepted -> send(RideCommand.MARK_ARRIVED) {
                rides.arrive(rideId, it.version).let { moved -> RideStateSnapshot(moved.state, moved.version) }
            }

            RideState.DriverArrived -> open(RideSheet.START_OTP)

            RideState.InProgress -> send(RideCommand.COMPLETE) {
                rides.complete(rideId, it.version).let { moved -> RideStateSnapshot(moved.state, moved.version) }
            }

            else -> Unit
        }
    }

    /**
     * Starts the ride against the rider's four-digit OTP (P-07 for a package's sender OTP).
     *
     * `423 otp-locked` once the five attempts are spent; the handoff then needs support, which is
     * what the resolved copy says.
     */
    fun startRide(otp: String) {
        val kind = mutableState.value.ride?.kind ?: RideKind.PASSENGER
        open(sheet = null)
        send(if (kind == RideKind.PACKAGE) RideCommand.VERIFY_PICKUP_OTP else RideCommand.START) {
            rides.start(rideId, kind, it.version, otp).let { moved -> RideStateSnapshot(moved.state, moved.version) }
        }
    }

    /**
     * **AL-47's confirm** — `POST /v1/fare/pay/driver-qr/confirm` (US-26.1).
     *
     * The payment moves to [PaymentState.DriverConfirmedQR], which is terminal and settles like
     * cash, so the earning posts (R-05). *"Not received"* raises a dispute instead: a ticket routed
     * Support → Finance, moving no money because the platform holds none of it.
     */
    fun confirmQrPayment(received: Boolean) {
        mutableState.update { it.copy(sheet = null, busy = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(busy = false) } }) {
            val settled = if (received) {
                rides.confirmQrPayment(rideId).state == PaymentState.DriverConfirmedQR
            } else {
                // The dispute is the answer too: the ride is off this driver's hands either way,
                // and a Support → Finance ticket is not something they wait on at the kerb.
                rides.disputeQrPayment(rideId, note = null)
                true
            }

            // No re-read. The ride is still `PaymentPending` until fare-svc's settlement reaches
            // ride-svc through the outbox, and refreshing into that window would raise the sheet
            // again on a driver who has just answered it.
            mutableState.update { it.copy(busy = false, qrAttested = settled, finished = settled) }
        }
    }

    /**
     * AL-48's call-type chooser, and the dial it produces.
     *
     * [CallType.FREE_VOIP] mints the WebRTC session; [CallType.DIRECT_DIAL] hands the real number
     * to the platform dialler and records that it happened. The log is best-effort — a failure to
     * write `comms.call_log` must never stop a driver reaching the rider — so the number is put in
     * the state either way.
     */
    fun call(type: CallType) {
        val ride = mutableState.value.ride ?: return
        open(sheet = null)
        launchGuarded {
            runCatching { contact.startCall(rideId, ride.kind, type) }
            if (type == CallType.DIRECT_DIAL) {
                mutableState.update { it.copy(dialNumber = ride.counterpartyPhone) }
            }
        }
    }

    /** US-12.8's driver SOS. Active trip only, which is the only state this screen exists in. */
    fun triggerSos() {
        val at = mutableState.value.position?.asPoint() ?: return
        launchGuarded { contact.triggerSos(rideId, at) }
    }

    /** Raises one of SCR-DA-015's three sheets, or `null` to take whichever is up back down. */
    fun open(sheet: RideSheet?) {
        mutableState.update { it.copy(sheet = sheet) }
    }

    /** Clears a consumed dial number or the last failure. */
    fun consume() {
        mutableState.update { it.copy(dialNumber = null, error = null) }
    }

    /**
     * Sends one command, guarded by the local table and answered by the server's own state.
     *
     * A command the table refuses is not sent: `verdict` saves a round trip the driver would spend
     * fifteen seconds of a ride on. A command the table allows and the server refuses is still the
     * server's answer — the projection applies it and flags it rather than dropping it.
     */
    private fun send(command: RideCommand, block: suspend (RideDetail) -> RideStateSnapshot) {
        val ride = mutableState.value.ride ?: return
        if (projection?.canSend(command) == false) return

        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(busy = false) } }) {
            // The response IS the new state and version — every ride-svc mutation answers with
            // both (D3' §0), so re-reading `GET …/state` afterwards would be a round trip spent
            // learning what the last one already said.
            val moved = block(ride)
            projection?.onServerState(moved)
            mutableState.update { it.advancedTo(moved) }
        }
    }

    /** The driver's own position, and the poll that stands in for the hub. */
    private fun observeDevice() {
        viewModelScope.launch {
            location.fixes.collect { fix -> mutableState.update { it.copy(position = fix) } }
        }
        viewModelScope.launch {
            while (isActive) {
                delay(POLL)
                val current = mutableState.value
                if (current.finished || current.busy || current.ride == null) continue
                runCatching { rides.snapshot(rideId) }.getOrNull()?.let { snapshot ->
                    projection?.onServerState(snapshot)
                    mutableState.update { it.advancedTo(snapshot) }
                }
            }
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the user raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    private companion object {

        /**
         * Five seconds.
         *
         * Slow enough to be a fallback rather than a second dispatch plane, fast enough that a
         * passenger cancelling reaches the driver before they finish parking.
         */
        val POLL: Duration = 5.seconds
    }
}
