package lk.mageride.shared.domain.ride

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import lk.mageride.shared.data.models.ride.PackageStatus

// Package delivery's two OTP-gated handoffs (P-06, P-07, P-10, D5' §11; AL-33's three sheets).
//
// A package ride traverses the SAME state machine as a passenger ride (ADD Appendix B.2 invariant
// 6). What differs is that two of its edges are gated: `DriverArrived → InProgress` needs the
// sender's pickup OTP, and `InProgress → Completed` needs the recipient's delivery OTP or a proof
// photo. Both OTPs are four digits, HMAC-hashed at rest with a pepper, and leave the server exactly
// once — so the device never holds anything it could check an attempt against. Every verification
// is a round trip.
//
// FIVE ATTEMPTS PER GATE (P-07). The sixth is `423 otp-locked` and the handoff goes to the admin
// queue: a courier standing at a door cannot be allowed to brute-force a four-digit code, and a
// recipient who genuinely cannot produce it needs a human, not another try.

/** Which of a package's two handoffs an OTP belongs to (P-07). */
public enum class PackageGate {

    /** The sender releases the package: `DriverArrived → InProgress`. */
    PICKUP,

    /** The recipient accepts it: `InProgress → Completed` (AL-21). */
    DELIVERY,
}

/**
 * How a gate stands.
 *
 * @property attemptsUsed Rejected attempts so far.
 * @property maxAttempts The budget, [PackageHandoff.MAX_OTP_ATTEMPTS] unless a test says otherwise.
 * @property verified Whether the correct OTP has been accepted.
 * @property lockedByServer Whether the server has answered `423 otp-locked`. Set independently of
 *   [attemptsUsed] because a driver can resume a handoff on a second device, whose local count
 *   starts at zero while the server's does not.
 */
public data class PackageGateState(
    val attemptsUsed: Int = 0,
    val maxAttempts: Int = PackageHandoff.MAX_OTP_ATTEMPTS,
    val verified: Boolean = false,
    val lockedByServer: Boolean = false,
) {

    /** Attempts left before the gate locks. */
    public val attemptsRemaining: Int get() = (maxAttempts - attemptsUsed).coerceAtLeast(0)

    /** Whether the budget is spent, locally or per the server. */
    public val isLocked: Boolean get() = lockedByServer || attemptsRemaining == 0

    /** Whether another OTP may be submitted. */
    public val isOpen: Boolean get() = !verified && !isLocked
}

/**
 * What a screen should do about a gate.
 *
 * The three outcomes are genuinely different actions, which is why they are types and not a
 * boolean: [Open] shows the keypad, [Verified] advances the sheet, and [AdminQueue] stops asking
 * and routes to support.
 */
public sealed interface PackageGateOutcome {

    /**
     * The gate is live.
     *
     * @property attemptsRemaining What to warn with as it runs down.
     */
    public data class Open(val attemptsRemaining: Int) : PackageGateOutcome

    /** The OTP was accepted; this handoff is done. */
    public data object Verified : PackageGateOutcome

    /**
     * Five attempts are spent (P-07). The handoff needs support intervention.
     *
     * The driver is not stuck with the package: ops resolve it from the admin queue. The screen's
     * job is to say so rather than to offer a sixth box.
     */
    public data object AdminQueue : PackageGateOutcome
}

/**
 * Both gates plus the proof-photo fallback, for one package ride.
 *
 * @property pickup The sender's gate.
 * @property delivery The recipient's gate.
 * @property proofPhotoStored Whether a `delivery_photo` artifact has been uploaded (P-10) — the
 *   fallback when nobody is there to read the code out.
 */
public data class PackageHandoffState(
    val pickup: PackageGateState = PackageGateState(),
    val delivery: PackageGateState = PackageGateState(),
    val proofPhotoStored: Boolean = false,
) {

    /** How far the package has got, in the terms `RideDetail.packageStatus` uses. */
    public val status: PackageStatus
        get() = when {
            delivery.verified || proofPhotoStored -> PackageStatus.Delivered
            pickup.verified -> PackageStatus.InTransit
            else -> PackageStatus.PickupPending
        }

    /** Whether the pickup OTP has released the package, i.e. the ride may start. */
    public val canStart: Boolean get() = pickup.verified

    /**
     * Whether "Delivery completed" may be tapped (AL-33 sheet 3).
     *
     * Either proof satisfies it: the recipient's OTP, or an uploaded photo when there is no
     * recipient to ask (P-10). The receipt then reports `otp_verified` or `photo_proof`.
     */
    public val canComplete: Boolean get() = delivery.verified || proofPhotoStored

    /** [PackageGateOutcome] for [gate] — what its sheet should render. */
    public fun outcomeOf(gate: PackageGate): PackageGateOutcome {
        val state = stateOf(gate)
        return when {
            state.verified -> PackageGateOutcome.Verified
            state.isLocked -> PackageGateOutcome.AdminQueue
            else -> PackageGateOutcome.Open(state.attemptsRemaining)
        }
    }

    /** The raw gate state. */
    public fun stateOf(gate: PackageGate): PackageGateState = when (gate) {
        PackageGate.PICKUP -> pickup
        PackageGate.DELIVERY -> delivery
    }

    internal fun withGate(gate: PackageGate, updated: PackageGateState): PackageHandoffState = when (gate) {
        PackageGate.PICKUP -> copy(pickup = updated)
        PackageGate.DELIVERY -> copy(delivery = updated)
    }
}

/**
 * The attempt budget for one package ride's two handoffs (P-07).
 *
 * Held by [RideProjection] for a `package` ride, so a screen asking "may I complete?" gets one
 * answer covering both the ride state and the handoff rather than having to remember to check two
 * things.
 *
 * @param maxAttempts Per gate. The spec's five; a parameter so a test can exhaust it in three
 *   lines rather than five.
 */
public class PackageHandoff(private val maxAttempts: Int = MAX_OTP_ATTEMPTS) {

    private val mutableState = MutableStateFlow(
        PackageHandoffState(
            pickup = PackageGateState(maxAttempts = maxAttempts),
            delivery = PackageGateState(maxAttempts = maxAttempts),
        ),
    )

    /** Both gates, as one value. */
    public val state: StateFlow<PackageHandoffState> = mutableState.asStateFlow()

    /**
     * Whether [otp] is worth sending: four digits, and the gate still open.
     *
     * A malformed entry is refused **without spending an attempt** — the budget exists to stop
     * guessing, and a typo the client can see is not a guess. The server applies its own check;
     * this one keeps a fat-fingered five-digit paste from costing the driver a try.
     */
    public fun canSubmit(gate: PackageGate, otp: String): Boolean =
        mutableState.value.stateOf(gate).isOpen && isWellFormed(otp)

    /**
     * Records a rejected OTP (`400 validation-failed` / `401`), spending one attempt.
     *
     * @return The gate's outcome afterwards — [PackageGateOutcome.AdminQueue] on the fifth.
     */
    public fun onRejected(gate: PackageGate): PackageGateOutcome = update(gate) {
        it.copy(attemptsUsed = (it.attemptsUsed + 1).coerceAtMost(it.maxAttempts))
    }

    /** Records the accepted OTP. The gate is done and no further attempt is spendable. */
    public fun onVerified(gate: PackageGate): PackageGateOutcome = update(gate) { it.copy(verified = true) }

    /**
     * Records a `423 otp-locked` from the server (P-07).
     *
     * The server's count is authoritative — it survives a reinstall and a second device, and this
     * projection does not.
     */
    public fun onServerLocked(gate: PackageGate): PackageGateOutcome = update(gate) {
        it.copy(lockedByServer = true, attemptsUsed = it.maxAttempts)
    }

    /** Records a stored proof photo (P-10), which unlocks completion without the delivery OTP. */
    public fun onProofPhotoStored() {
        mutableState.value = mutableState.value.copy(proofPhotoStored = true)
    }

    private fun update(gate: PackageGate, block: (PackageGateState) -> PackageGateState): PackageGateOutcome {
        val current = mutableState.value
        val next = current.withGate(gate, block(current.stateOf(gate)))
        mutableState.value = next
        return next.outcomeOf(gate)
    }

    public companion object {

        /** Attempts per gate before `423 otp-locked` and the admin queue (P-07, D5' §11). */
        public const val MAX_OTP_ATTEMPTS: Int = 5

        /** Digits in a package OTP (P-07). Four, not the six of a sign-in code (D5' §14.1). */
        public const val OTP_LENGTH: Int = 4

        /** Whether [otp] is the four-digit shape the contract's `OtpAttempt` declares. */
        public fun isWellFormed(otp: String): Boolean = otp.length == OTP_LENGTH && otp.all { it in '0'..'9' }
    }
}
