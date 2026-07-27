package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.Timestamp
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * D5' §5.2's cadence table, re-declared independently and swept.
 *
 * The same technique as `RideTransitionTableTest` (C015) and `PaymentTransitionTableTest` (C016):
 * [EXPECTED] is typed out from the spec rather than derived from the enum, so a phase whose range
 * or coalesce rule drifts fails the build instead of quietly changing how often a driver's handset
 * talks to the broker.
 */
class AdaptiveRateEngineTest {

    /** Phase → (min, max, default, coalesces) exactly as D5' §5.2 + AL-12 + D5' §5.1 fix them. */
    private val expected: Map<GpsPhase, Row> = EXPECTED_TABLE

    private fun engine(phase: GpsPhase = GpsPhase.STANDBY_IDLE, config: AdaptiveRateConfig = AdaptiveRateConfig()) =
        AdaptiveRateEngine(config, phase)

    @Test
    fun every_phase_in_the_table_is_modelled_and_none_is_invented() {
        assertEquals(expected.keys, GpsPhase.entries.toSet())
        assertEquals(8, GpsPhase.entries.size, "D5' §5.2 has eight rows")
    }

    @Test
    fun the_cadence_engine_emits_the_documented_interval_for_every_phase() {
        expected.forEach { (phase, row) ->
            assertEquals(row.min, phase.minInterval, "$phase min")
            assertEquals(row.max, phase.maxInterval, "$phase max")
            assertEquals(row.default, phase.defaultInterval, "$phase default")
            assertEquals(row.default, engine(phase).interval(MQTT_EPOCH), "$phase engine")
        }
    }

    @Test
    fun the_two_anchors_later_specs_pin_are_the_ones_the_engine_uses() {
        // AL-12: "1 call/s within an admin-configurable radius of pickup/drop-off" and "Mode C
        // idle-standby = 60 s".
        assertEquals(1.seconds, GpsPhase.NEAR_PICKUP_GEOFENCE.defaultInterval)
        assertEquals(1.seconds, GpsPhase.NEAR_DROP_GEOFENCE.defaultInterval)
        assertEquals(60.seconds, GpsPhase.STANDBY_IDLE.defaultInterval)
        // D5' §5.1's base cadence: moving = 1 call / 4 s.
        assertEquals(4.seconds, GpsPhase.IN_PROGRESS.defaultInterval)
        assertEquals(4.seconds, GpsPhase.ACCEPTED_PICKUP_BOUND.defaultInterval)
    }

    @Test
    fun only_the_two_geofence_phases_burst() {
        val bursting = GpsPhase.entries.filter { it.isGeofenceBurst }.toSet()

        assertEquals(setOf(GpsPhase.NEAR_PICKUP_GEOFENCE, GpsPhase.NEAR_DROP_GEOFENCE), bursting)
    }

    @Test
    fun the_coalesce_column_matches_the_table() {
        expected.forEach { (phase, row) ->
            val rule = if (row.coalesces) CoalesceRule.SKIP_IF_STATIONARY else CoalesceRule.NONE
            assertEquals(rule, phase.coalesce, "$phase")
        }
    }

    @Test
    fun the_freshness_window_is_twice_the_expected_interval() {
        // ADD §7.5.1: a candidate whose last sample is older than 2 × expectedInterval is excluded
        // from the scoring round.
        assertEquals(10.seconds, GpsPhase.CANDIDATE_IN_POOL.freshnessWindow())
        assertEquals(8.seconds, engine(GpsPhase.IN_PROGRESS).freshnessWindow(MQTT_EPOCH))
    }

    @Test
    fun a_server_hint_overrides_the_phase_default() {
        val engine = engine(GpsPhase.STANDBY_IDLE)
        val hint = setPosRate(2000.milliseconds)

        assertEquals(2.seconds, engine.onCadenceHint(hint, MQTT_EPOCH))
        assertEquals(2.seconds, engine.interval(MQTT_EPOCH))
    }

    @Test
    fun a_hint_reverts_to_the_phase_default_when_it_expires() {
        val engine = engine(GpsPhase.STANDBY_IDLE)
        engine.onCadenceHint(setPosRate(2.seconds, expiresAt = MQTT_EPOCH + 5.minutes), MQTT_EPOCH)

        assertEquals(2.seconds, engine.interval(MQTT_EPOCH + 1.minutes))
        assertEquals(60.seconds, engine.interval(MQTT_EPOCH + 5.minutes), "a lapsed hint is not a cadence")
        assertNull(engine.activeHint)
    }

    @Test
    fun a_phase_change_drops_the_hint_the_server_set_for_the_previous_one() {
        val engine = engine(GpsPhase.CANDIDATE_IN_POOL)
        engine.onCadenceHint(setPosRate(2.seconds), MQTT_EPOCH)

        assertEquals(1.seconds, engine.onPhase(GpsPhase.NEAR_PICKUP_GEOFENCE))
        assertNull(engine.activeHint)
    }

    @Test
    fun a_hint_is_clamped_into_the_range_the_broker_will_tolerate() {
        val engine = engine()

        assertEquals(1.seconds, engine.onCadenceHint(setPosRate(10.milliseconds), MQTT_EPOCH))
        assertEquals(5.minutes, engine.onCadenceHint(setPosRate(30.minutes), MQTT_EPOCH))
    }

    @Test
    fun an_already_expired_hint_is_ignored_outright() {
        val engine = engine(GpsPhase.IN_PROGRESS)

        val interval = engine.onCadenceHint(setPosRate(1.seconds, expiresAt = MQTT_EPOCH - 1.seconds), MQTT_EPOCH)

        assertEquals(4.seconds, interval)
        assertNull(engine.activeHint)
    }

    @Test
    fun the_first_fix_of_a_session_always_publishes() {
        assertEquals(PublishDecision.Publish, engine().decide(MQTT_EPOCH, COLOMBO, lastPublished = null))
    }

    @Test
    fun a_fix_inside_the_cadence_interval_is_too_soon() {
        val engine = engine(GpsPhase.IN_PROGRESS)
        val last = PublishedFix(COLOMBO, MQTT_EPOCH)

        val decision = engine.decide(MQTT_EPOCH + 3.seconds, COLOMBO, last)

        assertEquals(SkipReason.TOO_SOON, assertIs<PublishDecision.Skip>(decision).reason)
    }

    @Test
    fun a_stationary_vehicle_coalesces_only_in_the_phases_that_say_so() {
        val last = PublishedFix(COLOMBO, MQTT_EPOCH)
        val barelyMoved = COLOMBO.copy(lat = COLOMBO.lat + 0.0001) // ~11 m, under the 25 m rule
        val later = MQTT_EPOCH + 2.minutes

        val idle = engine(GpsPhase.STANDBY_IDLE).decide(later, barelyMoved, last)
        assertEquals(SkipReason.COALESCED, assertIs<PublishDecision.Skip>(idle).reason)

        val inRide = engine(GpsPhase.IN_PROGRESS).decide(later, barelyMoved, last)
        assertEquals(PublishDecision.Publish, inRide, "freshness beats bytes once a ride is running")
    }

    @Test
    fun a_vehicle_that_moved_more_than_twenty_five_metres_publishes() {
        val last = PublishedFix(COLOMBO, MQTT_EPOCH)
        val moved = COLOMBO.copy(lat = COLOMBO.lat + 0.0005) // ~55 m

        assertEquals(
            PublishDecision.Publish,
            engine(GpsPhase.STANDBY_IDLE).decide(MQTT_EPOCH + 2.minutes, moved, last),
        )
    }

    @Test
    fun the_optional_heartbeat_defeats_coalescing_when_an_operator_asks_for_it() {
        val config = AdaptiveRateConfig(coalesceHeartbeat = 5.minutes)
        val engine = engine(GpsPhase.STANDBY_IDLE, config)
        val last = PublishedFix(COLOMBO, MQTT_EPOCH)

        val early = engine.decide(MQTT_EPOCH + 2.minutes, COLOMBO, last)

        assertEquals(SkipReason.COALESCED, assertIs<PublishDecision.Skip>(early).reason)
        assertEquals(PublishDecision.Publish, engine.decide(MQTT_EPOCH + 5.minutes, COLOMBO, last))
    }

    @Test
    fun the_client_stops_at_the_five_messages_a_second_broker_ceiling() {
        // D-17. Being throttled by EMQX also emits `mqtt.rate_violation` into audit.events, so a
        // client that leans on the broker to rate-limit it is generating a fraud signal.
        val engine = engine(GpsPhase.NEAR_PICKUP_GEOFENCE)
        repeat(MqttRateLimits.LIVE_MSG_PER_SECOND) { engine.onPublished(MQTT_EPOCH + (it * 10).milliseconds) }

        val decision = engine.decide(MQTT_EPOCH + 100.milliseconds, COLOMBO, lastPublished = null)

        assertEquals(SkipReason.CEILING, assertIs<PublishDecision.Skip>(decision).reason)
        assertEquals(5, engine.publishesInWindow(MQTT_EPOCH + 100.milliseconds))
    }

    @Test
    fun the_ceiling_window_slides() {
        val engine = engine(GpsPhase.NEAR_PICKUP_GEOFENCE)
        repeat(MqttRateLimits.LIVE_MSG_PER_SECOND) { engine.onPublished(MQTT_EPOCH) }

        assertEquals(0, engine.publishesInWindow(MQTT_EPOCH + 1.seconds))
        assertEquals(
            PublishDecision.Publish,
            engine.decide(MQTT_EPOCH + 1.seconds, COLOMBO, PublishedFix(COLOMBO, MQTT_EPOCH)),
        )
    }

    private fun setPosRate(interval: Duration, expiresAt: Timestamp? = null) = MqttCommand.SetPosRate(
        interval,
        MqttCommandEnvelope(cmd = MqttCommandName.SET_POS_RATE.wire, expiresAt = expiresAt),
    )

    private data class Row(val min: Duration, val max: Duration, val default: Duration, val coalesces: Boolean)

    private companion object {
        @Suppress("unused")
        val EXPECTED_TABLE: Map<GpsPhase, Row> = mapOf(
            GpsPhase.STANDBY_IDLE to Row(30.seconds, 60.seconds, 60.seconds, coalesces = true),
            GpsPhase.STANDBY_MOVING to Row(5.seconds, 10.seconds, 10.seconds, coalesces = true),
            GpsPhase.CANDIDATE_IN_POOL to Row(2.seconds, 5.seconds, 5.seconds, coalesces = false),
            GpsPhase.ACCEPTED_PICKUP_BOUND to Row(2.seconds, 4.seconds, 4.seconds, coalesces = false),
            GpsPhase.NEAR_PICKUP_GEOFENCE to Row(1.seconds, 2.seconds, 1.seconds, coalesces = false),
            GpsPhase.IN_PROGRESS to Row(2.seconds, 4.seconds, 4.seconds, coalesces = false),
            GpsPhase.NEAR_DROP_GEOFENCE to Row(1.seconds, 2.seconds, 1.seconds, coalesces = false),
            GpsPhase.PAYMENT_PENDING to Row(30.seconds, 30.seconds, 30.seconds, coalesces = true),
        )
    }
}
