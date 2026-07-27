package lk.mageride.shared.domain.dispatch

import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.flow
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.DeclineRideOfferRequest
import lk.mageride.shared.data.models.ride.RideDetail
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.ExperimentalTime

/**
 * How an offer ended.
 *
 * **[Taken] and [Expired] are different things and are never collapsed.** The server keeps them
 * apart — `409 offer-already-accepted` against `410 offer-expired` — because the driver-facing
 * consequence differs: one says somebody was faster, the other says nobody was. Both put the
 * driver back in the pool, and a driver app that showed "too slow" for a ride nobody took would be
 * lying about their own acceptance rate.
 */
public sealed interface OfferOutcome {

    /**
     * This driver won the atomic accept (R-02).
     *
     * @property ride The full aggregate, so the ride screen needs no second read.
     */
    public data class Won(val ride: RideDetail) : OfferOutcome

    /** `409 offer-already-accepted` — another driver's conditional UPDATE landed first. */
    public data object Taken : OfferOutcome

    /**
     * The fifteen seconds ran out.
     *
     * Either `410 offer-expired` from the server, or — with no call made at all — the local TTL,
     * which is the honest answer when the deadline is already behind us.
     */
    public data object Expired : OfferOutcome

    /** The driver passed. No penalty (D5' §7); dispatch cascades to the next candidate. */
    public data object Declined : OfferOutcome

    /**
     * `402 insufficient-wallet` — the D-08 daily-fee gate, not a dispatch failure.
     *
     * The offer is lost, but the reason is a balance the driver can top up, so the app should say
     * that rather than show a generic error (D5' §2, §3.2 "2nd-trip wallet < daily fee").
     */
    public data object WalletBlocked : OfferOutcome

    /**
     * Something else went wrong — offline, 5xx, a `409 version-conflict`.
     *
     * @property error What failed.
     */
    public data class Failed(val error: MageRideError) : OfferOutcome
}

/** Where the driver's offer slot is. */
public sealed interface OfferSessionState {

    /** No offer in hand. The driver is available and dispatch may reserve them. */
    public data object Idle : OfferSessionState

    /**
     * An offer is live and the countdown is running.
     *
     * @property offer The offer.
     */
    public data class Live(val offer: RideOffer) : OfferSessionState

    /**
     * The accept or decline is in flight.
     *
     * @property offer The offer being decided.
     */
    public data class Deciding(val offer: RideOffer) : OfferSessionState

    /**
     * The driver won it and now holds a ride.
     *
     * @property ride The ride they won.
     */
    public data class Won(val ride: RideDetail) : OfferSessionState
}

/**
 * The driver's single offer slot: one live offer, a 15-second countdown, and the two ways of
 * losing it.
 *
 * **A driver holds at most one live offer** (ADD Appendix B.2 invariant 3; enforced server-side by
 * `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')` plus the Redis reservation lock,
 * D5' §3.6). This class mirrors that: [onOfferPushed] replaces whatever was held, because a second
 * offer reaching the device means the first is already dead — dispatch cannot have reserved this
 * driver twice.
 *
 * **An expired offer is not sent.** [accept] compares the deadline against the clock before it
 * touches the network: the accept would earn a `410` anyway, and the round trip is fifteen seconds
 * of the driver's next offer. The session returns to [OfferSessionState.Idle] so the UI moves on.
 *
 * @param api The ride-svc client, resolved lazily for the same reason C014 defers `IamApi` — the
 *   graph is complete by the time an offer arrives, and is not while it is being built.
 * @param clock Wall clock. Injectable so a test can drive the TTL on virtual time.
 */
@OptIn(ExperimentalTime::class)
public class OfferSession(
    private val api: () -> RideApi,
    private val clock: () -> Timestamp = { Clock.System.now() },
) {

    private val mutableState = MutableStateFlow<OfferSessionState>(OfferSessionState.Idle)

    /** Where the offer slot is. */
    public val state: StateFlow<OfferSessionState> = mutableState.asStateFlow()

    /** The live offer, or `null`. */
    public val offer: RideOffer?
        get() = when (val current = mutableState.value) {
            is OfferSessionState.Live -> current.offer
            is OfferSessionState.Deciding -> current.offer
            else -> null
        }

    /**
     * Whether dispatch may reserve this driver for the next candidate round.
     *
     * `true` in [OfferSessionState.Idle] only. A won offer is *not* ready: the driver has a ride,
     * and ADD Appendix B.2 invariant 2 gives them at most one.
     */
    public val isReadyForNextOffer: Boolean get() = mutableState.value is OfferSessionState.Idle

    /** Takes delivery of an offer, from FCM or MQTT. */
    public fun onOfferPushed(offer: RideOffer) {
        mutableState.value = OfferSessionState.Live(offer)
    }

    /**
     * Drops the live offer without telling the server.
     *
     * For the local countdown reaching zero and for an `offer.expired` frame arriving — in both
     * cases the server has already released the driver, and declining an offer that is gone would
     * be a `410` for nothing.
     */
    public fun onExpired() {
        if (mutableState.value !is OfferSessionState.Won) mutableState.value = OfferSessionState.Idle
    }

    /**
     * Takes the offer (R-02, `POST /v1/rides/{rideId}/offer/{driverId}/accept`).
     *
     * Reads the ride's `version` from `GET /v1/rides/{rideId}/state` when the offer envelope did
     * not carry one — `dispatch.events` `offer.created` does not, and the accept body requires it
     * (R-14). That read is inside the fifteen seconds, so it is done once and only when needed.
     *
     * @return [OfferOutcome.Won] and nothing else means the driver has the ride.
     */
    public suspend fun accept(): OfferOutcome {
        val live = liveOfferOrDrop() ?: return OfferOutcome.Expired

        mutableState.value = OfferSessionState.Deciding(live)
        return runDecision {
            val version = live.version ?: api().getRideState(live.rideId).version
            val won = api().acceptRideOffer(
                rideId = live.rideId,
                driverId = live.driverId,
                request = AcceptRideOfferRequest(offerId = live.offerId, version = version),
            )
            OfferOutcome.Won(won.ride)
        }
    }

    /**
     * Passes on the offer (`POST /v1/rides/{rideId}/offer/{driverId}/decline`).
     *
     * The driver is back in the pool either way: a decline that fails to reach the server is
     * released by the offer's own TTL fifteen seconds later, so the local slot is not held open
     * waiting for a call that already failed.
     */
    public suspend fun decline(): OfferOutcome {
        val live = liveOfferOrDrop() ?: return OfferOutcome.Expired

        mutableState.value = OfferSessionState.Deciding(live)
        return runDecision {
            api().declineRideOffer(
                rideId = live.rideId,
                driverId = live.driverId,
                request = DeclineRideOfferRequest(offerId = live.offerId),
            )
            OfferOutcome.Declined
        }
    }

    /**
     * The countdown, for a ring or a bar.
     *
     * Emits what is left of the live offer every [interval] and completes at zero — including
     * immediately, if the offer is already gone. Cancelling the collector stops it; it holds no
     * state of its own, so a screen may collect it, drop it and collect it again.
     */
    public fun countdown(interval: Duration = COUNTDOWN_INTERVAL): Flow<Duration> = flow {
        var remaining = offer?.remaining(clock()) ?: Duration.ZERO
        while (remaining > Duration.ZERO) {
            emit(remaining)
            delay(minOf(interval, remaining))
            remaining = offer?.remaining(clock()) ?: Duration.ZERO
        }
        emit(Duration.ZERO)
    }

    /** Applies the ride version this driver already learned from another read (R-14). */
    public fun onVersionKnown(version: RideVersion) {
        val current = mutableState.value
        if (current is OfferSessionState.Live) {
            mutableState.value = OfferSessionState.Live(current.offer.copy(version = version))
        }
    }

    private suspend fun runDecision(block: suspend () -> OfferOutcome): OfferOutcome {
        val outcome = try {
            block()
        } catch (cause: MageRideError) {
            cause.toOutcome()
        }
        mutableState.value = when (outcome) {
            is OfferOutcome.Won -> OfferSessionState.Won(outcome.ride)

            // Everything else — taken, expired, declined, no wallet, a network failure — leaves
            // the driver with no offer. Holding a dead one back would cost them the next round.
            else -> OfferSessionState.Idle
        }
        return outcome
    }

    /**
     * The live offer, or `null` — and if the fifteen seconds have gone, the slot is freed on the
     * way out.
     *
     * This is the DoD's local guard, and both [accept] and [decline] go through it: an offer whose
     * deadline is behind us is not ours to act on, whatever the ride still says. Sending it would
     * earn a `410` and cost the driver a round trip they need for the next offer.
     */
    private fun liveOfferOrDrop(): RideOffer? {
        val live = (mutableState.value as? OfferSessionState.Live)?.offer
        val lapsed = live != null && live.isExpired(clock())
        if (lapsed) mutableState.value = OfferSessionState.Idle
        return if (lapsed) null else live
    }

    private companion object {

        /** Fine enough that a 15-second ring does not visibly step, cheap enough to run for 15 s. */
        val COUNTDOWN_INTERVAL: Duration = 250.milliseconds
    }
}

/**
 * The two ways of losing an offer, plus the wallet gate, keyed the way C013 mapped them.
 *
 * `409` and `410` are distinct types by construction ([MageRideError.Conflict] /
 * [MageRideError.Gone]); the code is checked as well so a *different* `409` — a
 * `version-conflict`, say — is reported as the failure it is rather than as a race this driver
 * lost.
 */
private fun MageRideError.toOutcome(): OfferOutcome = when {
    this is MageRideError.Conflict && code == ErrorCode.OFFER_ALREADY_ACCEPTED -> OfferOutcome.Taken
    this is MageRideError.Gone -> OfferOutcome.Expired
    this is MageRideError.PaymentRequired && code == ErrorCode.INSUFFICIENT_WALLET -> OfferOutcome.WalletBlocked
    else -> OfferOutcome.Failed(this)
}
