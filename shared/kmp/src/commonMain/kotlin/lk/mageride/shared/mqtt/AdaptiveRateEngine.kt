package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.domain.geo.distanceMetres
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

// Phase-aware GPS cadence (R-07, AL-12; ADD §7.5.1, D5' §5.2).
//
// A single fleet-blended rate is wrong in both directions at once: it over-pays ingest for a
// parked vehicle and under-samples one that is three hundred metres from a pickup. So the cadence
// follows the driver's workflow phase, the server pushes corrections on `veh/{vehicleId}/cmd`, and
// the device falls back to the phase default when no hint is in force.
//
// THE CLIENT-SIDE RATE IS A COOPERATIVE CONTRACT (D-17). EMQX enforces 5 msg/s per vehicle at the
// broker and emits `mqtt.rate_violation` into `audit.events` when a client exceeds it; being
// throttled is an anti-tamper signal, not a retry hint. [AdaptiveRateEngine] therefore refuses a
// publish that would breach the ceiling rather than letting the broker do it.

/** What suppresses a publish that the cadence would otherwise allow (D5' §5.2, "Coalesce"). */
public enum class CoalesceRule {
    /** Publish on every tick — freshness matters more than bytes. */
    NONE,

    /** Skip when the vehicle has not moved 25 m since the last published fix. */
    SKIP_IF_STATIONARY,
}

/**
 * The eight rows of D5' §5.2, as data.
 *
 * **How [defaultInterval] is derived.** The table gives ranges, not single numbers, and two later
 * sources pin points inside them: AL-12 fixes the near-pickup/near-drop burst at **1 call/s** and
 * Mode C idle standby at **60 s**, and D5' §5.1's base cadence gives 4 s moving, 10 s stationary,
 * 60 s idle standby. The one rule that satisfies every one of those anchors is *slow end of the
 * range, except inside the geofence burst*:
 *
 * | Phase | Range | Default | Anchor |
 * |---|---|---|---|
 * | Standby idle | 30–60 s | **60 s** | AL-12, §5.1 idle standby |
 * | Standby moving | 5–10 s | **10 s** | §5.1 stationary |
 * | Candidate in pool | 2–5 s | **5 s** | — (server hints during a round) |
 * | Accepted → PickupBound | 2–4 s | **4 s** | §5.1 moving |
 * | Near-pickup geofence | 1–2 s | **1 s** | AL-12 burst |
 * | InProgress | 2–4 s | **4 s** | §5.1 moving |
 * | Near-drop geofence | 1–2 s | **1 s** | AL-12 burst |
 * | PaymentPending | 30 s | **30 s** | — |
 *
 * A server hint overrides all of it; this is what the device uses when none is in force.
 *
 * @property minInterval Fastest cadence the table allows for the phase.
 * @property maxInterval Slowest.
 * @property coalesce Whether a stationary vehicle may skip a tick.
 * @property isGeofenceBurst Whether this is one of AL-12's two 1 s burst phases.
 */
public enum class GpsPhase(
    public val minInterval: Duration,
    public val maxInterval: Duration,
    public val coalesce: CoalesceRule,
    public val isGeofenceBurst: Boolean = false,
) {
    /** App online, no session (Mode C idle standby). */
    STANDBY_IDLE(30.seconds, 60.seconds, CoalesceRule.SKIP_IF_STATIONARY),

    /** A Mode A/B tracking session is running and the vehicle is above 5 km/h. */
    STANDBY_MOVING(5.seconds, 10.seconds, CoalesceRule.SKIP_IF_STATIONARY),

    /** `driver:availability = AVAILABLE` and inside a candidate window — scoring freshness. */
    CANDIDATE_IN_POOL(2.seconds, 5.seconds, CoalesceRule.NONE),

    /** `ride.state = Accepted`: heading to the pickup. */
    ACCEPTED_PICKUP_BOUND(2.seconds, 4.seconds, CoalesceRule.NONE),

    /** Inside the pickup geofence — the AL-12 burst. */
    NEAR_PICKUP_GEOFENCE(1.seconds, 2.seconds, CoalesceRule.NONE, isGeofenceBurst = true),

    /** `ride.state = InProgress`. */
    IN_PROGRESS(2.seconds, 4.seconds, CoalesceRule.NONE),

    /** Inside the drop-off geofence — the AL-12 burst. */
    NEAR_DROP_GEOFENCE(1.seconds, 2.seconds, CoalesceRule.NONE, isGeofenceBurst = true),

    /** `ride.state = PaymentPending`: the ride is over bar the money. */
    PAYMENT_PENDING(30.seconds, 30.seconds, CoalesceRule.SKIP_IF_STATIONARY),
    ;

    /** The cadence to use with no server hint in force — see the class KDoc for the derivation. */
    public val defaultInterval: Duration get() = if (isGeofenceBurst) minInterval else maxInterval

    /**
     * How stale a sample may get before `dispatch-svc` drops the driver from a scoring round.
     *
     * "Any `dispatch.candidate_scores` evaluation against a driver whose last `pos/live` sample is
     * older than `2 × expectedInterval` excludes that driver from the round" (ADD §7.5.1). The
     * driver app can show that it is about to go stale instead of quietly stopping being offered
     * work.
     */
    public fun freshnessWindow(interval: Duration = defaultInterval): Duration = interval * 2
}

/**
 * The tunables around the cadence table.
 *
 * Everything here is either admin-configurable or a platform ceiling; nothing is a taste
 * judgement. Read it from the server's config where one exists rather than holding this
 * instance for the life of the app.
 *
 * @property geofenceRadiusMetres When the near-pickup / near-drop burst starts. **AL-12 makes it
 *   admin-configurable with a 150 m default**; ADD §7.5.1 and D5' §5.2 still print `<300 m` in the
 *   trigger column of the same rows. AL-12 is the later amendment and wins — see the C017 handoff.
 *   The phase itself is server-computed and arrives as a cadence hint; this radius is what the
 *   client uses to anticipate it.
 * @property coalesceMinMovementMetres The 25 m in D5' §5.2's coalesce column.
 * @property ceilingPerSecond The broker's per-vehicle ceiling (D-17). The engine keeps the client
 *   under it, retries included.
 * @property minInterval Floor for a server hint. 1 s is the fastest cadence any spec asks for; a
 *   hint below it would be a misconfiguration, and honouring it would trip the broker ceiling.
 * @property maxInterval Ceiling for a server hint, so a bad value cannot silently stop publishing.
 * @property coalesceHeartbeat Optional: publish anyway once this long has passed, even when the
 *   vehicle has not moved. **Off by default, which is exactly what D5' §5.2 says** — a stationary
 *   vehicle publishes nothing. Turn it on only if an operator needs proof-of-life on the position
 *   plane rather than from the LWT.
 */
public data class AdaptiveRateConfig(
    public val geofenceRadiusMetres: Int = GEOFENCE_RADIUS_M,
    public val coalesceMinMovementMetres: Double = COALESCE_MIN_MOVEMENT_M,
    public val ceilingPerSecond: Int = MqttRateLimits.LIVE_MSG_PER_SECOND,
    public val minInterval: Duration = MIN_INTERVAL,
    public val maxInterval: Duration = MAX_INTERVAL,
    public val coalesceHeartbeat: Duration? = null,
) {
    public companion object {
        /** AL-12's admin-configurable burst radius, default 150 m. */
        public const val GEOFENCE_RADIUS_M: Int = 150

        /** D5' §5.2's coalesce threshold. */
        public const val COALESCE_MIN_MOVEMENT_M: Double = 25.0

        /** The fastest cadence any spec asks for (AL-12's 1 s burst). */
        public val MIN_INTERVAL: Duration = 1.seconds

        /** A sanity ceiling on a server hint. */
        public val MAX_INTERVAL: Duration = 5.minutes
    }
}

/** Why a fix was not published. */
public enum class SkipReason {
    /** The cadence interval has not elapsed since the last publish. */
    TOO_SOON,

    /** The phase coalesces and the vehicle has not moved far enough (D5' §5.2). */
    COALESCED,

    /** Publishing now would breach the broker's 5 msg/s ceiling (D-17). */
    CEILING,
}

/** What [AdaptiveRateEngine.decide] concluded about one candidate fix. */
public sealed interface PublishDecision {

    /** Publish it. */
    public data object Publish : PublishDecision

    /** Hold it back, for [reason]. */
    public data class Skip(public val reason: SkipReason) : PublishDecision
}

/** A fix that was published, and when. */
public data class PublishedFix(public val point: GeoPoint, public val at: Timestamp)

/**
 * Decides how often the device publishes, and whether this particular fix goes out.
 *
 * Pure and clock-injected — every method takes `now` — so the whole cadence table is testable
 * without a broker, a radio or a coroutine. The foreground service that owns the position stream
 * drives it:
 *
 * ```
 * engine.onPhase(GpsPhase.IN_PROGRESS)                  // ride state changed
 * engine.onCadenceHint(hint, now)                       // setPosRate arrived on veh/{id}/cmd
 * when (engine.decide(now, fix, lastPublished)) {
 *     PublishDecision.Publish -> { publish(fix); engine.onPublished(now) }
 *     is PublishDecision.Skip  -> Unit
 * }
 * ```
 *
 * **A hint outlives nothing.** `expiresAt` on the command envelope is honoured: once it passes,
 * the cadence reverts to the phase default rather than leaving a vehicle stuck at a rate the
 * server set for a situation that has ended. A phase change also drops the hint — the server sets
 * cadence *for* a phase, so a hint that survived into the next one would be describing a state
 * the vehicle has left.
 *
 * Not thread-safe; one per vehicle, on the position stream's coroutine.
 *
 * @param config Tunables and ceilings.
 * @param initialPhase Where the driver starts — online, no session.
 */
public class AdaptiveRateEngine(
    private val config: AdaptiveRateConfig = AdaptiveRateConfig(),
    initialPhase: GpsPhase = GpsPhase.STANDBY_IDLE,
) {
    private val publishes = ArrayDeque<Timestamp>()

    private var currentPhase: GpsPhase = initialPhase
    private var hintInterval: Duration? = null
    private var hintExpiresAt: Timestamp? = null

    /** The workflow phase in force. */
    public val phase: GpsPhase get() = currentPhase

    /** The server hint in force, or `null` when the phase default applies. */
    public val activeHint: Duration? get() = hintInterval

    /**
     * Moves to a new phase, dropping any hint the server set for the previous one.
     *
     * @return the cadence now in force.
     */
    public fun onPhase(phase: GpsPhase): Duration {
        if (phase != currentPhase) {
            currentPhase = phase
            hintInterval = null
            hintExpiresAt = null
        }
        return phase.defaultInterval
    }

    /**
     * Applies a `setPosRate` hint from `veh/{vehicleId}/cmd`.
     *
     * The interval is clamped into `[config.minInterval, config.maxInterval]`: a hint below the
     * floor would put the client through the broker's ceiling — where it is suppressed *and*
     * audited — and one above the ceiling would look like a vehicle that had gone dark.
     *
     * @return the cadence now in force.
     */
    public fun onCadenceHint(hint: MqttCommand.SetPosRate, now: Timestamp): Duration {
        if (hint.envelope.isExpired(now)) return interval(now)
        hintInterval = hint.interval.coerceIn(config.minInterval, config.maxInterval)
        hintExpiresAt = hint.envelope.expiresAt
        return hintInterval ?: currentPhase.defaultInterval
    }

    /** Forgets the server hint — the cadence reverts to the phase default. */
    public fun clearHint() {
        hintInterval = null
        hintExpiresAt = null
    }

    /** The cadence in force at [now]: the live hint if one is, else the phase default. */
    public fun interval(now: Timestamp): Duration {
        val expiry = hintExpiresAt
        if (expiry != null && now >= expiry) clearHint()
        return hintInterval ?: currentPhase.defaultInterval
    }

    /**
     * How stale the last sample may get before dispatch stops scoring this driver (ADD §7.5.1).
     */
    public fun freshnessWindow(now: Timestamp): Duration = interval(now) * 2

    /**
     * Whether [candidate] should be published now.
     *
     * @param now Wall clock.
     * @param candidate Where the vehicle is.
     * @param lastPublished The previous published fix, or `null` if none has gone out yet — the
     *   first fix of a session always publishes.
     */
    // Guard clauses: each `return` is one rule from D5' §5.2, and nesting them would put the
    // ceiling check three levels in from the rule it enforces.
    @Suppress("ReturnCount")
    public fun decide(now: Timestamp, candidate: GeoPoint, lastPublished: PublishedFix?): PublishDecision {
        if (lastPublished == null) return ceilingCheck(now)

        if (now - lastPublished.at < interval(now)) return PublishDecision.Skip(SkipReason.TOO_SOON)

        if (currentPhase.coalesce == CoalesceRule.SKIP_IF_STATIONARY && !heartbeatDue(now, lastPublished)) {
            val moved = distanceMetres(lastPublished.point, candidate)
            if (moved < config.coalesceMinMovementMetres) return PublishDecision.Skip(SkipReason.COALESCED)
        }

        return ceilingCheck(now)
    }

    /** Records a publish — including a retry, which the broker counts against the same ceiling. */
    public fun onPublished(now: Timestamp) {
        publishesInWindow(now)
        publishes.addLast(now)
    }

    /** How many publishes are inside the current one-second ceiling window. */
    public fun publishesInWindow(now: Timestamp): Int {
        while (publishes.isNotEmpty() && now - publishes.first() >= CEILING_WINDOW) {
            publishes.removeFirst()
        }
        return publishes.size
    }

    private fun heartbeatDue(now: Timestamp, lastPublished: PublishedFix): Boolean {
        val heartbeat = config.coalesceHeartbeat ?: return false
        return now - lastPublished.at >= heartbeat
    }

    private fun ceilingCheck(now: Timestamp): PublishDecision = if (publishesInWindow(now) >= config.ceilingPerSecond) {
        PublishDecision.Skip(SkipReason.CEILING)
    } else {
        PublishDecision.Publish
    }

    public companion object {
        /** The window the broker measures its 5 msg/s ceiling over (ADD §7.5.2's tumbling window). */
        public val CEILING_WINDOW: Duration = 1000.milliseconds
    }
}
