package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.ride.PackageStatus
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The package OTP gates (P-06, P-07, P-10, D5' §11, AL-33).
 *
 * Both OTPs are hashed at rest with a pepper and leave the server exactly once, so the device has
 * nothing to check an entry against — every attempt is a round trip, and the budget is what stops a
 * courier at a door working through ten thousand four-digit codes.
 */
class PackageHandoffTest {

    @Test
    fun a_fresh_handoff_has_five_attempts_on_each_gate() {
        val handoff = PackageHandoff()

        for (gate in PackageGate.entries) {
            assertEquals(
                PackageGateOutcome.Open(PackageHandoff.MAX_OTP_ATTEMPTS),
                handoff.state.value.outcomeOf(gate),
            )
        }
        assertEquals(PackageStatus.PickupPending, handoff.state.value.status)
    }

    @Test
    fun the_fifth_rejection_locks_the_gate_and_surfaces_the_admin_queue() {
        val handoff = PackageHandoff()

        repeat(PackageHandoff.MAX_OTP_ATTEMPTS - 1) {
            val outcome = handoff.onRejected(PackageGate.DELIVERY)
            assertTrue(outcome is PackageGateOutcome.Open, "attempt ${it + 1} should have left the gate open")
        }

        assertEquals(PackageGateOutcome.AdminQueue, handoff.onRejected(PackageGate.DELIVERY))
        assertEquals(0, handoff.state.value.delivery.attemptsRemaining)
        assertFalse(handoff.canSubmit(PackageGate.DELIVERY, "1234"), "a locked gate takes no sixth attempt")
    }

    @Test
    fun locking_one_gate_leaves_the_other_alone() {
        val handoff = PackageHandoff()

        repeat(PackageHandoff.MAX_OTP_ATTEMPTS) { handoff.onRejected(PackageGate.PICKUP) }

        assertEquals(PackageGateOutcome.AdminQueue, handoff.state.value.outcomeOf(PackageGate.PICKUP))
        assertEquals(
            PackageGateOutcome.Open(PackageHandoff.MAX_OTP_ATTEMPTS),
            handoff.state.value.outcomeOf(PackageGate.DELIVERY),
        )
    }

    @Test
    fun a_server_lock_is_believed_even_when_this_device_has_attempts_left() {
        // The server's counter survives a reinstall and a second handset; the local one does not.
        val handoff = PackageHandoff()
        handoff.onRejected(PackageGate.PICKUP)

        assertEquals(PackageGateOutcome.AdminQueue, handoff.onServerLocked(PackageGate.PICKUP))
        assertTrue(handoff.state.value.pickup.isLocked)
    }

    @Test
    fun a_malformed_entry_is_refused_without_spending_an_attempt() {
        val handoff = PackageHandoff()

        assertFalse(handoff.canSubmit(PackageGate.PICKUP, "12345"), "the OTP is four digits (P-07)")
        assertFalse(handoff.canSubmit(PackageGate.PICKUP, "12a4"))
        assertFalse(handoff.canSubmit(PackageGate.PICKUP, ""))
        assertTrue(handoff.canSubmit(PackageGate.PICKUP, "0417"))

        // The budget exists to stop guessing, and a typo the client can see is not a guess.
        assertEquals(PackageHandoff.MAX_OTP_ATTEMPTS, handoff.state.value.pickup.attemptsRemaining)
    }

    @Test
    fun the_two_handoffs_walk_the_package_from_pending_to_delivered() {
        val handoff = PackageHandoff()

        assertFalse(handoff.state.value.canStart)

        handoff.onVerified(PackageGate.PICKUP)

        assertEquals(PackageStatus.InTransit, handoff.state.value.status)
        assertTrue(handoff.state.value.canStart)
        assertFalse(handoff.state.value.canComplete)

        handoff.onVerified(PackageGate.DELIVERY)

        assertEquals(PackageStatus.Delivered, handoff.state.value.status)
        assertTrue(handoff.state.value.canComplete)
    }

    @Test
    fun a_proof_photo_completes_a_delivery_the_recipient_cannot() {
        val handoff = PackageHandoff()
        handoff.onVerified(PackageGate.PICKUP)
        repeat(PackageHandoff.MAX_OTP_ATTEMPTS) { handoff.onRejected(PackageGate.DELIVERY) }

        assertEquals(PackageGateOutcome.AdminQueue, handoff.state.value.outcomeOf(PackageGate.DELIVERY))
        assertFalse(handoff.state.value.canComplete)

        // P-10: the recipient is absent, so the photo is the proof and the receipt reports
        // `photo_proof` instead of `otp_verified`.
        handoff.onProofPhotoStored()

        assertTrue(handoff.state.value.canComplete)
        assertEquals(PackageStatus.Delivered, handoff.state.value.status)
    }

    @Test
    fun a_verified_gate_takes_no_further_attempts() {
        val handoff = PackageHandoff()
        handoff.onVerified(PackageGate.PICKUP)

        assertFalse(handoff.canSubmit(PackageGate.PICKUP, "1234"))
        assertEquals(PackageGateOutcome.Verified, handoff.state.value.outcomeOf(PackageGate.PICKUP))
    }
}
