package lk.mageride.driver.delivery

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
import lk.mageride.driver.R
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.asPoint
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.CalleeRole
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.domain.ride.PackageGate
import lk.mageride.shared.domain.ride.PackageHandoff
import lk.mageride.shared.domain.ride.RideProjection
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-016a/b/c · the three delivery sheets** (AL-33, US-20.12/20.13).
 *
 * One view model for all three, because they are one job: *review → pick up → hand over*, over the
 * package ride's own state machine. Which sheet is up is **derived from the ride**, not held as a
 * step counter — `package.picked_up` is the `→ InProgress` move, so a driver whose app died between
 * the two doors comes back to the right sheet by reading the ride rather than by remembering
 * anything (P-07).
 *
 * **The attempt budget is `:shared`'s, not this screen's.** `PackageHandoff` (C015) is the P-07 rule
 * as a type — five attempts per gate, the sixth `423 otp-locked` and the handoff goes to the admin
 * queue — and `RideProjection` already owns one per package ride. Counting here as well would be a
 * second answer to a question the domain layer has, and the two would eventually disagree.
 *
 * **Both call buttons are a direct PSTN dial** and there is no chooser: AL-33 says the delivery
 * sheets place *"a real mobile-phone voice call"* to whichever end of the delivery was tapped,
 * which is a different decision from a ride's AL-48 Free-call/Normal-call pair. The log still goes
 * through `POST /v1/calls/start`, and still best-effort — a failure to write `comms.call_log` must
 * never stop a courier reaching a doorstep.
 */
// Thirteen members, and the count is AL-33's: three sheets with three different forward actions,
// two OTP gates that fail three distinct ways, a photograph, two call buttons and a cancel that is
// not a cancellation of the delivery. Splitting them would need a rule to keep the ride's version
// and the handoff's budget in step across the halves, which is the pair it is fatal to get wrong.
@OptIn(ExperimentalTime::class)
@Suppress("TooManyFunctions")
internal class DeliveryViewModel(
    private val rideId: Ulid,
    private val deliveries: DeliveryRepository,
    private val contact: RideContact,
    private val location: DriverLocationSource,
    private val proofs: ProofUploadQueue,
    private val captures: DocumentCaptureCoordinator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(DeliveryState(proof = proofs.pendingFor(rideId)))

    val state: StateFlow<DeliveryState> = mutableState.asStateFlow()

    /**
     * Seated on the first read and kept for the screen's life.
     *
     * Re-seating it on every refresh — which is what SCR-DA-015 does — would throw the handoff away
     * with it, and with the handoff the count of how many codes the driver has already got wrong.
     */
    private var projection: RideProjection? = null

    init {
        refresh()
        observeDevice()

        // SCR-DA-005 hands the proof photograph back here. Replayed, so a picture taken while this
        // screen was in the background — which it always is, the scanner being a destination —
        // still lands.
        viewModelScope.launch {
            captures.results.collect { result ->
                if (result.target == DocumentCaptureTarget.DELIVERY_PROOF) attachProof(result.image)
                captures.consume()
            }
        }
    }

    /** Re-reads the aggregate and folds it onto the sheets. */
    fun refresh() {
        launchGuarded {
            val detail = deliveries.detail(rideId)
            projection = projection?.also { it.onServerState(detail) } ?: RideProjection.of(detail)
            mutableState.update { it.applied(detail) }
        }
    }

    /**
     * The sheet's own forward action — one button per sheet, three different things.
     *
     * **Sheet 1 · Start delivery is local, and deliberately so.** Nothing on the wire corresponds
     * to a driver setting off: the parcel is released by the sender's OTP, and `POST …/arrive` is
     * the passenger flow's geofence marker, which D5' §11 skips outright by taking the package
     * straight from `Accepted` to `InProgress`. Sending anything here would be inventing a
     * transition to make a button feel busy.
     *
     * **Sheet 2 · Verify pickup** spends the sender's code. **Sheet 3 · Delivery completed** spends
     * the recipient's, or uploads the photograph that stands in for it (P-10).
     */
    fun advance() {
        if (!mutableState.value.canAdvance) return
        when (mutableState.value.sheet) {
            DeliverySheet.REVIEW -> mutableState.update { it.copy(started = true, error = null) }
            DeliverySheet.PICKUP -> verify(PackageGate.PICKUP)
            DeliverySheet.COMPLETE -> complete()
        }
    }

    /**
     * **Cancel on sheet 1** — the driver puts the delivery down before setting off.
     *
     * It does not cancel the *delivery*: AL-33 releases the request back to dispatch for the next
     * eligible driver. What a client can do is release it from this driver, which is what this
     * sends; see [DeliveryRepository.release] for why the re-dispatch half does not happen yet.
     */
    fun cancel() {
        val ride = mutableState.value.ride ?: return
        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(onFailure = { mutableState.value.copy(busy = false) }) {
            deliveries.release(rideId, ride.version)
            proofs.discard(rideId)
            mutableState.update { it.copy(busy = false, finished = true) }
        }
    }

    /** A digit was typed. Non-digits and anything past the fourth are dropped by the field. */
    fun onOtpChange(otp: String) {
        mutableState.update { it.copy(otp = otp, error = null) }
    }

    /** The 📷 **Photo proof** button. The scanner is SCR-DA-005's; this only says what it is for. */
    fun requestProofCapture() {
        captures.open(DocumentCaptureTarget.DELIVERY_PROOF)
    }

    /**
     * Files the photograph SCR-DA-005 handed back (P-10).
     *
     * It is **queued, not sent**: the upload is what completes the delivery, so it belongs to the
     * *"Delivery completed"* tap and not to the shutter. The driver can retake before committing.
     */
    fun attachProof(image: CapturedImage) {
        val entry = proofs.enqueue(
            rideId = rideId,
            image = image,
            at = mutableState.value.position?.asPoint(),
            capturedAt = now(),
        )
        mutableState.update { it.copy(proof = entry, error = null) }
    }

    /**
     * A call button — **a direct cellular dial of that party's real number** (AL-33).
     *
     * Distinct from a ride's masked-free VoIP choice: a courier needs the person at the door, and
     * the number is the one `RideDetail` carries from `Accepted` onward. The `comms.call_log` write
     * is best-effort, so the number reaches the dialler whether or not the log call succeeds.
     */
    fun call(party: DeliveryParty) {
        val number = mutableState.value.phoneOf(party) ?: return
        launchGuarded {
            runCatching { contact.startCall(rideId, party.calleeRole, CallType.DIRECT_DIAL) }
            mutableState.update { it.copy(dialNumber = number) }
        }
    }

    /** US-12.8's driver SOS — the same alarm SCR-DA-015 raises, on the same active trip. */
    fun triggerSos() {
        val at = mutableState.value.position?.asPoint() ?: return
        launchGuarded { contact.triggerSos(rideId, at) }
    }

    /** Clears a consumed dial number or the last failure. */
    fun consume() {
        mutableState.update { it.copy(dialNumber = null, error = null) }
    }

    /**
     * Spends one OTP attempt against [gate].
     *
     * The verdict is `:shared`'s: `canSubmit` refuses a malformed entry **without spending an
     * attempt** — the budget exists to stop guessing, and a typo is not a guess — and the outcome of
     * a rejection is what decides between *"wrong code, N left"* and the admin queue.
     */
    private fun verify(gate: PackageGate) {
        val handoff = projection?.handoff ?: return
        val otp = mutableState.value.otp
        if (!handoff.canSubmit(gate, otp)) return

        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(onFailure = { failure -> otpFailure(gate, failure) }) {
            val moved = when (gate) {
                PackageGate.PICKUP -> deliveries.verifyPickup(rideId, otp)
                PackageGate.DELIVERY -> deliveries.verifyDelivery(rideId, otp)
            }
            handoff.onVerified(gate)
            projection?.onServerState(moved)
            mutableState.update { it.moved(RideStateSnapshot(moved.state, moved.version), handoff) }
        }
    }

    /**
     * Sheet 3's CTA — *"Delivery completed"*, which **replaces** the old *"Cash received (COD)"*.
     *
     * A photograph in hand takes the P-10 route and is itself the completion (Δ C037); otherwise the
     * recipient's OTP is. Neither settles the cash: AL-33 decoupled that, and uncollected COD is a
     * 24-hour timer's problem rather than this button's (P-14).
     */
    private fun complete() {
        val queued = mutableState.value.proof ?: return verify(PackageGate.DELIVERY)
        val handoff = projection?.handoff

        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(
            onFailure = {
                // The photograph goes back in the queue rather than being dropped: the driver is
                // still at the door, and the retry must not need the camera again.
                proofs.reschedule(queued.id)
                mutableState.value.copy(busy = false)
            },
        ) {
            val claimed = proofs.claim(queued.id) ?: queued
            val stored = deliveries.deliverWithProof(rideId, claimed)
            proofs.markUploaded(claimed.id)
            handoff?.onProofPhotoStored()

            // Δ C037 reports where the ride landed, so there is no re-read; a server that answers
            // the older shape leaves it absent and the five-second poll picks the move up instead.
            mutableState.update { current ->
                val next = current.copy(proof = null)
                val landed = stored.state?.let {
                    // The version stays the one the screen was holding when it is not reported: a
                    // number invented here would be echoed on the next mutation as if it were the
                    // server's, which is exactly what R-14 exists to catch.
                    RideStateSnapshot(it, stored.version ?: next.ride?.version ?: 0)
                }
                if (landed == null) next.copy(busy = false) else next.moved(landed, handoff)
            }
        }
    }

    /**
     * A refused OTP, told apart by its code (D-26 — never by the server's English message).
     *
     * `423 otp-locked` is the server saying the budget is gone, which is authoritative over the
     * local count: a driver resuming on a second handset starts at zero attempts here and at five
     * there. Anything else that is not an `invalid-otp` is an ordinary failure and costs no attempt.
     */
    private fun otpFailure(gate: PackageGate, cause: Throwable): DeliveryState {
        val handoff = projection?.handoff
        when {
            cause is MageRideError.Locked || (cause as? MageRideError)?.code == ErrorCode.OTP_LOCKED ->
                handoff?.onServerLocked(gate)

            (cause as? MageRideError)?.code == ErrorCode.INVALID_OTP -> handoff?.onRejected(gate)
        }
        val current = mutableState.value
        return current.copy(busy = false, otp = "", gates = handoff?.state?.value ?: current.gates)
    }

    /** The driver's own position, and the poll that stands in for the SignalR hub. */
    private fun observeDevice() {
        viewModelScope.launch {
            location.fixes.collect { fix -> mutableState.update { it.copy(position = fix) } }
        }
        viewModelScope.launch {
            while (isActive) {
                delay(POLL)
                val current = mutableState.value
                if (current.finished || current.busy || current.ride == null) continue
                runCatching { deliveries.snapshot(rideId) }.getOrNull()?.let { snapshot ->
                    projection?.onServerState(snapshot)
                    mutableState.update { it.moved(snapshot, projection?.handoff) }
                }
            }
        }
    }

    private fun now(): Timestamp = Clock.System.now()

    /**
     * Runs [block], turning any failure into copy.
     *
     * [onFailure] answers the state the screen should be left in — and the copy is applied to it in
     * **one** write. Two writes would put a frame on screen showing a locked gate with no
     * explanation under it, which is the state a driver would be looking at when they read the
     * sheet and decide it is broken.
     */
    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the user raw.
    private fun launchGuarded(onFailure: (Throwable) -> DeliveryState? = { null }, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                val recovered = onFailure(cause) ?: mutableState.value
                mutableState.value = recovered.copy(error = messageFor(cause))
            }
        }
    }

    private companion object {

        /** Five seconds — SCR-DA-015's interval, for the same reason and the same fallback. */
        val POLL: Duration = 5.seconds
    }
}

/** Who a call button reaches, as `comms.call_log` records it. */
private val DeliveryParty.calleeRole: CalleeRole
    get() = when (this) {
        DeliveryParty.SENDER -> CalleeRole.SENDER
        DeliveryParty.RECIPIENT -> CalleeRole.RECIPIENT
    }

/**
 * Folds a whole-aggregate read onto the sheets.
 *
 * The **local** `started` flag is raised by a ride that has moved past the review: a driver
 * resuming a delivery already `InProgress` must not be shown sheet 1 again, and one at
 * `DriverArrived` has plainly set off.
 */
private fun DeliveryState.applied(detail: RideDetail): DeliveryState = copy(
    ride = detail,
    busy = false,
    started = started || detail.state != RideState.Accepted,
).let { next -> next.copy(finished = next.finished || next.handedOver) }

/**
 * Folds a server-confirmed state change on.
 *
 * `finished` is sticky for SCR-DA-015's reason: a delivery that has been handed over never
 * un-hands-over, and a late poll landing after the driver has been sent back to standby must not
 * undo that.
 */
private fun DeliveryState.moved(snapshot: RideStateSnapshot, handoff: PackageHandoff?): DeliveryState {
    val next = copy(
        busy = false,
        otp = "",
        ride = ride?.copy(state = snapshot.state, version = snapshot.version),
        gates = handoff?.state?.value ?: gates,
    )
    return next.copy(started = next.started || next.pickedUp, finished = next.finished || next.handedOver)
}

/** The trilingual copy for a failure — D-26's rule, plus the two this screen names itself. */
@Suppress("ReturnCount")
private fun messageFor(cause: Throwable): Int {
    val code = (cause as? MageRideError)?.code
    if (cause is MageRideError.Locked || code == ErrorCode.OTP_LOCKED) return R.string.delivery_otp_locked
    if (code == ErrorCode.INVALID_OTP) return R.string.delivery_otp_wrong
    return OnboardingErrors.messageFor(cause)
}
