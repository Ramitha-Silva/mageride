package lk.mageride.shared.domain.ride

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * What the client currently believes about one ride.
 *
 * @property rideId The ride.
 * @property kind Booking kind. Fixed for the ride's life (ADD Appendix B.2 invariant 6).
 * @property state Where the server last said it was.
 * @property version The optimistic-concurrency counter to echo on the next mutation (R-14).
 * @property offerExpiresAt The 15-second deadline, while an offer is live.
 */
public data class RideSnapshot(
    val rideId: Ulid,
    val kind: RideKind,
    val state: RideState,
    val version: RideVersion,
    val offerExpiresAt: Timestamp? = null,
) {
    /** Whether the ride is over (`Paid`, a cancellation, `Disputed`, …). */
    public val isTerminal: Boolean get() = state.isTerminal
}

/** Why a server snapshot changed nothing. */
public enum class RideUpdateIgnored {

    /**
     * The snapshot is older than what the projection already holds (R-14).
     *
     * Ordinary, not exceptional: SignalR frames, an FCM push and the reconnect poll all describe
     * the same ride, and the network does not promise to deliver them in order. The version is
     * what makes "older" answerable without guessing.
     */
    STALE_VERSION,

    /** Same version, same state — a duplicate delivery. */
    DUPLICATE,
}

/**
 * What a server snapshot did to the projection.
 *
 * Note what is **not** here: there is no "rejected" outcome. The server owns the state (R-01), so
 * a move this client does not recognise is applied and reported, never refused. A client that
 * dropped a transition it had not been taught would show a passenger a ride that had already been
 * cancelled.
 */
public sealed interface RideUpdate {

    /**
     * Nothing changed.
     *
     * @property reason Why the snapshot was passed over.
     */
    public data class Ignored(val reason: RideUpdateIgnored) : RideUpdate

    /**
     * The projection moved.
     *
     * @property from Where it was.
     * @property to Where it is.
     * @property trigger The ADD Appendix B.2 edge the server took, when the table draws exactly
     *   one between the two states. `null` when it draws none — and also when it draws more than
     *   one, since a bare state change does not say which: `Accepted → CancelledByDriver` is both
     *   a driver cancel and an expired grace window, and naming one of them would be a guess.
     *   Read [isKnownEdge], not `trigger != null`, to ask whether this build understands the move.
     */
    public data class Applied(val from: RideState, val to: RideState, val trigger: RideTrigger?) : RideUpdate {

        /**
         * Whether [RideTransitions] draws this move at all.
         *
         * `false` means the server and this build disagree about the shape of the machine — a
         * contract drift worth a log line and a metric, never a reason to ignore the server.
         */
        public val isKnownEdge: Boolean get() = RideTransitions.isReachable(from, to)
    }
}

/**
 * One ride, as the client understands it.
 *
 * **The projection never advances itself.** Every move comes from a server-confirmed snapshot —
 * a REST response, a SignalR `RideStateChanged` frame or an FCM push — through [onServerState].
 * There is deliberately no `apply(trigger)`: a client that could walk its own state machine would
 * eventually show a state ride-svc never wrote, and ride-svc is the sole writer (R-01). What the
 * transition table is for is [verdict], which answers "is this button worth tapping" *before* the
 * call, and naming the edge the server took *after* it.
 *
 * For a `package` ride the projection also owns the [PackageHandoff], so "may I complete?" is one
 * question with one answer rather than two checks a screen has to remember to make.
 *
 * @param initial Where the ride starts — normally from a `RideDetail` or the booking response.
 * @param handoff The package gates. Defaults to a fresh one for a `package` ride and `null`
 *   otherwise; pass one explicitly to restore a handoff mid-flight.
 * @param clock Wall clock, for the offer TTL. Injectable so a test can drive expiry.
 */
@OptIn(ExperimentalTime::class)
@Suppress("TooManyFunctions")
public class RideProjection(
    initial: RideSnapshot,
    public val handoff: PackageHandoff? = if (initial.kind == RideKind.PACKAGE) PackageHandoff() else null,
    private val clock: () -> Timestamp = { Clock.System.now() },
) {

    private val mutableState = MutableStateFlow(initial)

    /** Where the ride is. */
    public val snapshot: StateFlow<RideSnapshot> = mutableState.asStateFlow()

    /** The current state, for callers that want the one field. */
    public val state: RideState get() = mutableState.value.state

    /** The version to echo on the next mutation (R-14). */
    public val version: RideVersion get() = mutableState.value.version

    /**
     * Whether [command] is worth sending right now.
     *
     * Checks, in the order a user would notice them: the ride being over, the kind fence, the
     * offer TTL, the transition table and finally the package handoff.
     */
    public fun verdict(command: RideCommand, now: Timestamp = clock()): RideCommandVerdict {
        val current = mutableState.value
        rejectionFor(command, current, now)?.let { return RideCommandVerdict.Rejected(it) }
        return RideCommandVerdict.Allowed
    }

    /** Shorthand for `verdict(command).isAllowed`, for a screen wiring an enabled flag. */
    public fun canSend(command: RideCommand, now: Timestamp = clock()): Boolean = verdict(command, now).isAllowed

    /** Every command this ride will accept right now, for rendering an action bar. */
    public fun availableCommands(actor: RideActor, now: Timestamp = clock()): Set<RideCommand> =
        RideCommand.entries.filterTo(mutableSetOf()) { it.actor == actor && canSend(it, now) }

    /**
     * Applies a state the **server** reported.
     *
     * @param state Where ride-svc says the ride is.
     * @param version The version that state was written at.
     * @param offerExpiresAt The offer deadline, when the snapshot carried one. `null` leaves the
     *   held value alone while the ride is still `Offered` and clears it once it is not — an
     *   `Accepted` frame has no deadline to report and the countdown must stop regardless.
     */
    public fun onServerState(state: RideState, version: RideVersion, offerExpiresAt: Timestamp? = null): RideUpdate {
        val current = mutableState.value
        ignoredReason(current, state, version)?.let { return RideUpdate.Ignored(it) }

        mutableState.value = current.copy(
            state = state,
            version = version,
            offerExpiresAt = offerExpiresAt ?: current.offerExpiresAt.takeIf { state == RideState.Offered },
        )
        return RideUpdate.Applied(
            from = current.state,
            to = state,
            trigger = triggerBetween(current.state, state),
        )
    }

    /** Applies the `RideStateChange` every simple transition answers with. */
    public fun onServerState(change: RideStateChange): RideUpdate = onServerState(change.state, change.version)

    /** Applies the cheap `GET /v1/rides/{rideId}/state` poll, deadline included. */
    public fun onServerState(snapshot: RideStateSnapshot): RideUpdate =
        onServerState(snapshot.state, snapshot.version, snapshot.offerExpiresAt)

    /** Applies a full read. */
    public fun onServerState(detail: RideDetail): RideUpdate =
        onServerState(detail.state, detail.version, detail.offerExpiresAt)

    /**
     * How the ride ends if the driver's LWT grace runs out from here (R-16).
     *
     * @param offlineSince When the broker reported the driver offline.
     */
    public fun graceDeadline(offlineSince: Timestamp): Timestamp? = RideGrace.deadline(state, offlineSince)

    private fun ignoredReason(current: RideSnapshot, state: RideState, version: RideVersion): RideUpdateIgnored? =
        when {
            version < current.version -> RideUpdateIgnored.STALE_VERSION
            version == current.version && state == current.state -> RideUpdateIgnored.DUPLICATE
            else -> null
        }

    private fun rejectionFor(command: RideCommand, current: RideSnapshot, now: Timestamp): RideCommandRejection? =
        when {
            current.isTerminal -> RideCommandRejection.RIDE_TERMINAL

            !command.appliesTo(current.kind) -> RideCommandRejection.WRONG_KIND

            command.needsLiveOffer && current.offerExpiresAt == null -> RideCommandRejection.NO_LIVE_OFFER

            // The DoD's local guard: once the fifteen seconds are gone the offer is not ours to take,
            // whatever the ride still says. Sending it would earn a `410` and cost the driver the
            // round trip they need for the next offer.
            command.needsLiveOffer && current.offerExpiresAt!! <= now -> RideCommandRejection.OFFER_EXPIRED

            !RideTransitions.isLegal(current.state, command.trigger) -> RideCommandRejection.ILLEGAL_TRANSITION

            else -> packageRejectionFor(command)
        }

    private fun packageRejectionFor(command: RideCommand): RideCommandRejection? {
        val gates = handoff?.state?.value ?: return null
        return when (command) {
            RideCommand.VERIFY_PICKUP_OTP ->
                if (gates.pickup.isOpen) null else RideCommandRejection.OTP_LOCKED

            RideCommand.VERIFY_DELIVERY_OTP ->
                if (gates.delivery.isOpen) null else RideCommandRejection.OTP_LOCKED

            // AL-33 sheet 3: "Delivery completed" needs the recipient's OTP or a proof photo.
            RideCommand.COMPLETE ->
                if (gates.canComplete) null else RideCommandRejection.PACKAGE_HANDOFF_INCOMPLETE

            else -> null
        }
    }

    /**
     * The Appendix B.2 edge that connects two states, if there is exactly one.
     *
     * Several triggers can land in the same place from the same state — `Accepted` reaches
     * `CancelledByDriver` through both a driver cancel and an expired grace — and a bare state
     * change does not say which. Reporting `null` rather than guessing keeps
     * [RideUpdate.Applied.isKnownEdge] meaning "this build knows this move" and not "this build
     * picked one".
     */
    private fun triggerBetween(from: RideState, to: RideState): RideTrigger? = RideTransitions.EDGES
        .filter { it.from == from && it.to == to }
        .singleOrNull()
        ?.trigger

    public companion object {

        /** Starts a projection from a full ride read. */
        public fun of(detail: RideDetail, clock: () -> Timestamp = { Clock.System.now() }): RideProjection =
            RideProjection(
                initial = RideSnapshot(
                    rideId = detail.rideId,
                    kind = detail.kind,
                    state = detail.state,
                    version = detail.version,
                    offerExpiresAt = detail.offerExpiresAt,
                ),
                clock = clock,
            )
    }
}
