package lk.mageride.driver.delivery

import lk.mageride.driver.onboarding.testImage
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * P-10's proof queue — `mobile_db_schema.md` §3.6's verbs, in memory (see [ProofUploadQueue]).
 *
 * What is being asserted is the behaviour a durable table would have to keep if it replaced this
 * one: one photograph per delivery, a failed upload keeps its file, and an accepted one is dropped.
 */
class ProofUploadQueueTest {

    private val queue = ProofUploadQueue()

    @Test
    fun a_retake_replaces_the_photograph_rather_than_adding_a_second() {
        queue.enqueue(Fixtures.RIDE_ID, testImage("first.jpg"), at = null, capturedAt = Fixtures.NOW)
        queue.enqueue(Fixtures.RIDE_ID, testImage("second.jpg"), at = null, capturedAt = Fixtures.NOW)

        // The second picture is the driver saying the first one was wrong; keeping both would put
        // a rejected shot in front of a §11.14 dispute.
        assertEquals("second.jpg", queue.pendingFor(Fixtures.RIDE_ID)?.image?.fileName)
    }

    @Test
    fun a_claim_counts_the_attempt_and_a_reschedule_keeps_the_file() {
        val entry = queue.enqueue(
            rideId = Fixtures.RIDE_ID,
            image = testImage("proof.jpg"),
            at = GeoPoint(Fixtures.PICKUP.lat, Fixtures.PICKUP.lng),
            capturedAt = Fixtures.NOW,
        )

        assertEquals(1, queue.claim(entry.id)?.attempts)
        queue.reschedule(entry.id)

        val waiting = assertNotNull(queue.pendingFor(Fixtures.RIDE_ID))
        assertEquals(ProofUploadState.PENDING, waiting.state)
        assertEquals(1, waiting.attempts, "the count survives, so a second failure is visible")
        assertNotNull(waiting.at, "captured_geo is part of the proof (D5' §11)")
    }

    @Test
    fun an_uploaded_photograph_is_dropped_and_a_released_delivery_forgets_its_own() {
        val uploaded = queue.enqueue(Fixtures.RIDE_ID, testImage("a.jpg"), at = null, capturedAt = Fixtures.NOW)
        queue.markUploaded(uploaded.id)
        assertNull(queue.pendingFor(Fixtures.RIDE_ID), "§4.3 keeps no UPLOADED row")

        queue.enqueue(Fixtures.RIDE_ID, testImage("b.jpg"), at = null, capturedAt = Fixtures.NOW)
        queue.discard(Fixtures.RIDE_ID)
        assertNull(queue.pendingFor(Fixtures.RIDE_ID))
    }
}
