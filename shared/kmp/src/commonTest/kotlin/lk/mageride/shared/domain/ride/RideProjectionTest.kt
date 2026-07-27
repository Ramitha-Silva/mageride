package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RideStateChange
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

private const val FUZZ_SEED = 20260728
private const val FUZZ_FRAMES = 2_000

/**
 * The projection: what the client believes, and what it will let a screen ask for.
 *
 * The fence this file exists to hold is *"the client models state; the server owns it"*. There is
 * no way to advance a [RideProjection] except by handing it a state the server reported, and the
 * tests below check both halves of that — a server move is always applied, even one this build has
 * never heard of, and a local command is never more than a guess about what the server will allow.
 */
@OptIn(ExperimentalTime::class)
class RideProjectionTest {

    @Test
    fun a_server_state_change_moves_the_projection_and_names_the_edge() {
        val projection = projectionAt(RideState.DriverArrived, version = 7)

        val update = projection.onServerState(RideStateChange(TEST_RIDE_ID, RideState.InProgress, version = 8))

        assertEquals(
            RideUpdate.Applied(RideState.DriverArrived, RideState.InProgress, RideTrigger.RIDE_STARTED),
            update,
        )
        assertEquals(RideState.InProgress, projection.state)
        assertEquals(8, projection.version)
    }

    @Test
    fun an_older_version_is_ignored_rather_than_applied_backwards() {
        val projection = projectionAt(RideState.InProgress, version = 8)

        // SignalR, FCM and the reconnect poll all describe the same ride and none of them promises
        // ordering. R-14's version is what makes "this frame is old news" answerable.
        val update = projection.onServerState(RideState.DriverArrived, version = 7)

        assertEquals(RideUpdate.Ignored(RideUpdateIgnored.STALE_VERSION), update)
        assertEquals(RideState.InProgress, projection.state)
        assertEquals(8, projection.version)
    }

    @Test
    fun a_repeat_of_the_frame_we_already_have_changes_nothing() {
        val projection = projectionAt(RideState.Accepted, version = 4)

        val update = projection.onServerState(RideState.Accepted, version = 4)

        assertEquals(RideUpdate.Ignored(RideUpdateIgnored.DUPLICATE), update)
    }

    @Test
    fun a_transition_this_build_does_not_know_is_still_applied_and_is_flagged() {
        val projection = projectionAt(RideState.Requested, version = 1)

        // Nothing in Appendix B.2 goes straight from Requested to Completed. A client that dropped
        // the frame would show a ride that had already ended, so it is applied and reported.
        val update = projection.onServerState(RideState.Completed, version = 2)

        val applied = assertIs<RideUpdate.Applied>(update)
        assertFalse(applied.isKnownEdge, "the table draws no Requested → Completed edge")
        assertEquals(RideState.Completed, projection.state)
    }

    @Test
    fun a_state_two_triggers_can_reach_is_known_but_leaves_the_trigger_unnamed() {
        val projection = projectionAt(RideState.Accepted, version = 3)

        // Accepted → CancelledByDriver is both a driver cancel and an expired grace window, and a
        // bare state change does not say which. Guessing one would be worse than saying nothing.
        val applied = assertIs<RideUpdate.Applied>(
            projection.onServerState(RideState.CancelledByDriver, version = 4),
        )

        assertTrue(applied.isKnownEdge)
        assertNull(applied.trigger)
    }

    @Test
    fun a_terminal_ride_accepts_no_command_at_all() {
        for (state in RideState.entries.filter { it.isTerminal }) {
            val projection = projectionAt(state)
            for (command in RideCommand.entries) {
                assertEquals(
                    RideCommandVerdict.Rejected(RideCommandRejection.RIDE_TERMINAL),
                    projection.verdict(command),
                    "$command was offered on terminal $state",
                )
            }
        }
    }

    @Test
    fun a_package_ride_starts_through_the_pickup_otp_and_never_through_start() {
        val packageRide = projectionAt(RideState.DriverArrived, kind = RideKind.PACKAGE)

        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.WRONG_KIND),
            packageRide.verdict(RideCommand.START),
        )
        assertEquals(RideCommandVerdict.Allowed, packageRide.verdict(RideCommand.VERIFY_PICKUP_OTP))
    }

    @Test
    fun a_passenger_ride_has_no_package_commands() {
        val ride = projectionAt(RideState.InProgress, kind = RideKind.PASSENGER)

        for (command in listOf(RideCommand.VERIFY_DELIVERY_OTP, RideCommand.VERIFY_PICKUP_OTP)) {
            assertEquals(
                RideCommandVerdict.Rejected(RideCommandRejection.WRONG_KIND),
                ride.verdict(command),
                "$command is package-only (P-06/P-07)",
            )
        }
        assertEquals(RideCommandVerdict.Allowed, ride.verdict(RideCommand.COMPLETE))
    }

    @Test
    fun a_package_cannot_complete_until_the_delivery_otp_or_a_proof_photo_lands() {
        val handoff = PackageHandoff()
        val ride = packageProjectionAt(RideState.InProgress, handoff)

        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.PACKAGE_HANDOFF_INCOMPLETE),
            ride.verdict(RideCommand.COMPLETE),
        )

        // P-10: nobody at the door, so the photo is the proof.
        handoff.onProofPhotoStored()

        assertEquals(RideCommandVerdict.Allowed, ride.verdict(RideCommand.COMPLETE))
    }

    @Test
    fun a_locked_otp_gate_stops_the_projection_offering_another_attempt() {
        val handoff = PackageHandoff()
        val ride = packageProjectionAt(RideState.DriverArrived, handoff)

        repeat(PackageHandoff.MAX_OTP_ATTEMPTS) { handoff.onRejected(PackageGate.PICKUP) }

        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.OTP_LOCKED),
            ride.verdict(RideCommand.VERIFY_PICKUP_OTP),
        )
    }

    @Test
    fun an_expired_offer_cannot_be_accepted_locally() {
        val deadline = RIDE_EPOCH + 15.seconds
        var now = RIDE_EPOCH
        val projection = projectionAt(RideState.Offered, offerExpiresAt = deadline) { now }

        assertEquals(RideCommandVerdict.Allowed, projection.verdict(RideCommand.ACCEPT_OFFER))

        now = deadline

        // At the deadline, not a millisecond after it: the server's own guard is
        // `offer_expires_at > now()`, so the boundary belongs to the expiry.
        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.OFFER_EXPIRED),
            projection.verdict(RideCommand.ACCEPT_OFFER),
        )
        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.OFFER_EXPIRED),
            projection.verdict(RideCommand.DECLINE_OFFER),
        )
    }

    @Test
    fun accepting_needs_an_offer_to_accept() {
        val projection = projectionAt(RideState.Matching)

        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.NO_LIVE_OFFER),
            projection.verdict(RideCommand.ACCEPT_OFFER),
        )
    }

    @Test
    fun a_command_the_table_draws_no_edge_for_is_rejected_as_an_illegal_transition() {
        val projection = projectionAt(RideState.Accepted)

        // Completing a ride that has not started is `409 illegal-transition` server-side; there is
        // no reason to spend the round trip finding that out.
        assertEquals(
            RideCommandVerdict.Rejected(RideCommandRejection.ILLEGAL_TRANSITION),
            projection.verdict(RideCommand.COMPLETE),
        )
        assertEquals(RideCommandVerdict.Allowed, projection.verdict(RideCommand.MARK_ARRIVED))
    }

    @Test
    fun the_action_bar_only_ever_offers_edges_appendix_b_2_draws() {
        for (state in RideState.entries) {
            val projection = projectionAt(state, offerExpiresAt = RIDE_EPOCH + 15.seconds)
            for (actor in RideActor.entries) {
                for (command in projection.availableCommands(actor)) {
                    assertEquals(actor, command.actor)
                    assertTrue(
                        RideTransitions.isLegal(state, command.trigger),
                        "$state offered $command, which fires ${command.trigger} — not an edge from $state",
                    )
                }
            }
        }
    }

    @Test
    fun the_grace_deadline_widens_as_the_ride_gets_further_along() {
        val offline = RIDE_EPOCH

        assertEquals(offline + 60.seconds, projectionAt(RideState.Accepted).graceDeadline(offline))
        assertEquals(offline + 120.seconds, projectionAt(RideState.DriverArrived).graceDeadline(offline))
        assertEquals(offline + 5.minutes, projectionAt(RideState.InProgress).graceDeadline(offline))
        assertEquals(offline + 10.minutes, projectionAt(RideState.PaymentPending).graceDeadline(offline))
        // Nothing is at stake before a driver is on the hook.
        assertEquals(null, projectionAt(RideState.Matching).graceDeadline(offline))
    }

    @Test
    fun the_version_never_goes_backwards_however_the_frames_arrive() {
        val random = Random(FUZZ_SEED)
        val projection = projectionAt(RideState.Requested, version = 1)

        repeat(FUZZ_FRAMES) {
            val state = RideState.entries[random.nextInt(RideState.entries.size)]
            val version = random.nextInt(1, 25)
            val before = projection.version

            val update = projection.onServerState(state, version)

            assertTrue(projection.version >= before, "the projection fell back from $before to ${projection.version}")
            if (update is RideUpdate.Applied) {
                // Whatever the server said, that is what the client now believes. The projection
                // has no opinion of its own to defend.
                assertEquals(state, projection.state)
                assertEquals(version, projection.version)
            }
        }
    }
}
