package lk.mageride.shared.db

import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * §1.4 + §4.1 — the write path that must never lose or duplicate a user action.
 *
 * The rules here are the ones R-14/R-18 rest on; `CommandOutboxDurabilityTest` (androidHostTest)
 * re-runs the load-bearing ones against a real SQLite file that is closed and reopened.
 */
class CommandOutboxTest {

    private fun outbox(store: FakeOutboxStore = FakeOutboxStore(), random: Random = Random(1)) =
        CommandOutbox(store, random = random)

    @Test
    fun the_same_idempotency_key_is_queued_once_however_many_times_it_is_offered() {
        val store = FakeOutboxStore()
        val outbox = outbox(store)

        outbox.enqueue(command("K1"))
        val second = outbox.enqueue(command("K1").copy(requestBody = """{"different":true}"""))

        assertEquals(1, store.rows.size)
        // The FIRST body wins — a re-tap must replay the action the user was shown, not a new one.
        assertEquals("""{"clientRequestId":"K1"}""", second.requestBody)
    }

    @Test
    fun a_claimed_command_is_not_offered_to_a_second_drainer() {
        val outbox = outbox()
        outbox.enqueue(command("K1"))

        assertNotNull(outbox.claim("K1", T0))
        assertNull(outbox.claim("K1", T0))
        assertTrue(outbox.dispatchable(T0).isEmpty())
    }

    @Test
    fun claiming_counts_the_attempt() {
        val outbox = outbox()
        outbox.enqueue(command("K1"))

        assertEquals(1, outbox.claim("K1", T0)?.attempts)
    }

    @Test
    fun an_inflight_command_is_re_queued_after_a_restart_and_sent_again_under_the_same_key() {
        val store = FakeOutboxStore()
        val outbox = outbox(store)
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)

        // ---- process dies here, with the request's outcome unknown ----
        val recovered = outbox(store).recover(T0 + 1.minutes)

        assertContentEquals(listOf("K1"), recovered.map { it.idempotencyKey })
        val due = outbox(store).dispatchable(T0 + 1.minutes)
        assertContentEquals(listOf("K1"), due.map { it.idempotencyKey })
        // Same key on the wire: the server replays its stored response rather than executing twice
        // (ADD §11.13). That is what "exactly once" means for a queue that cannot know the outcome.
        assertEquals("K1", due.single().idempotencyKey)
    }

    @Test
    fun an_acked_command_is_never_offered_again_even_after_a_restart() {
        val store = FakeOutboxStore()
        outbox(store).also {
            it.enqueue(command("K1"))
            it.claim("K1", T0)
            it.onResponse("K1", status = 201, body = """{"rideId":"R1"}""", now = T0)
        }

        val afterRestart = outbox(store)
        assertTrue(afterRestart.recover(T0 + 1.minutes).isEmpty())
        assertTrue(afterRestart.dispatchable(T0 + 1.minutes).isEmpty())
    }

    @Test
    fun a_2xx_acks_and_keeps_the_server_response_verbatim() {
        val outbox = outbox()
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)

        val outcome = outbox.onResponse("K1", 200, """{"state":"Matching"}""", T0)

        val acked = assertIs<OutboxOutcome.Acked>(outcome)
        assertEquals(OutboxState.ACKED, acked.command.state)
        assertEquals("""{"state":"Matching"}""", acked.command.responseBody)
    }

    @Test
    fun a_non_retryable_4xx_fails_and_stays_for_the_user_to_dismiss() {
        val outbox = outbox()
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)

        assertIs<OutboxOutcome.Failed>(outbox.onResponse("K1", 422, null, T0))

        assertContentEquals(listOf("K1"), outbox.failed().map { it.idempotencyKey })
        outbox.prune(T0 + 48.hours)
        assertContentEquals(listOf("K1"), outbox.failed().map { it.idempotencyKey })

        outbox.dismiss("K1")
        assertTrue(outbox.failed().isEmpty())
    }

    @Test
    fun a_409_is_not_retried() {
        // C002's kernel returns 409 when an idempotency key is reused with a different body. No
        // amount of retrying fixes that, and retrying it forever hides the bug.
        val outbox = outbox()
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)

        assertIs<OutboxOutcome.Failed>(outbox.onResponse("K1", 409, null, T0))
    }

    @Test
    fun a_5xx_a_429_and_a_transport_failure_all_go_back_in_the_queue() {
        listOf(500, 503, 429, 408, 425).forEach { status ->
            val outbox = outbox()
            outbox.enqueue(command("K1"))
            outbox.claim("K1", T0)
            assertIs<OutboxOutcome.Retrying>(outbox.onResponse("K1", status, null, T0), "status $status")
        }

        val outbox = outbox()
        outbox.enqueue(command("K2"))
        outbox.claim("K2", T0)
        assertIs<OutboxOutcome.Retrying>(outbox.onTransportFailure("K2", T0))
    }

    @Test
    fun a_rescheduled_command_is_not_due_until_its_backoff_has_run() {
        val outbox = outbox()
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)
        val retry = assertIs<OutboxOutcome.Retrying>(outbox.onTransportFailure("K1", T0))

        assertTrue(retry.at > T0)
        assertTrue(outbox.dispatchable(retry.at - 1.seconds).isEmpty())
        assertContentEquals(listOf("K1"), outbox.dispatchable(retry.at).map { it.idempotencyKey })
    }

    @Test
    fun a_command_older_than_the_max_age_is_abandoned_rather_than_retried_forever() {
        val outbox = outbox()
        outbox.enqueue(command("K1", createdAt = T0))
        outbox.claim("K1", T0 + 25.hours)

        val outcome = outbox.onTransportFailure("K1", T0 + 25.hours)

        assertIs<OutboxOutcome.Abandoned>(outcome)
        assertEquals(OutboxState.ABANDONED, outcome.command.state)
    }

    @Test
    fun the_attempt_cap_abandons_a_command_that_fails_fast_in_a_loop() {
        val store = FakeOutboxStore()
        val policy = OutboxRetryPolicy(maxAttempts = 3)
        val outbox = CommandOutbox(store, policy, Random(7))
        outbox.enqueue(command("K1"))

        repeat(policy.maxAttempts) {
            outbox.claim("K1", T0)
            outbox.onTransportFailure("K1", T0)
        }

        assertEquals(OutboxState.ABANDONED, store.rows.getValue("K1").state)
    }

    @Test
    fun acked_rows_are_pruned_after_the_retention_window_and_not_before() {
        val store = FakeOutboxStore()
        val outbox = outbox(store)
        outbox.enqueue(command("K1"))
        outbox.claim("K1", T0)
        outbox.onResponse("K1", 200, null, T0)

        outbox.prune(T0 + 23.hours)
        assertEquals(1, store.rows.size)

        outbox.prune(T0 + 25.hours)
        assertTrue(store.rows.isEmpty())
    }

    @Test
    fun pending_commands_are_found_by_the_projection_row_they_belong_to() {
        val outbox = outbox()
        outbox.enqueue(command("K1", entityType = "address", entityId = "A1"))
        outbox.enqueue(command("K2", entityType = "address", entityId = "A2"))
        outbox.enqueue(command("K3", entityType = "ride", entityId = "A1"))

        assertContentEquals(listOf("K1"), outbox.pendingFor("address", "A1").map { it.idempotencyKey })

        outbox.claim("K1", T0)
        // Still pending as far as §4.2 is concerned — INFLIGHT is unfinished, not settled.
        assertContentEquals(listOf("K1"), outbox.pendingFor("address", "A1").map { it.idempotencyKey })

        outbox.onResponse("K1", 200, null, T0)
        assertTrue(outbox.pendingFor("address", "A1").isEmpty())
    }

    @Test
    fun commands_drain_in_the_order_the_user_issued_them() {
        val outbox = outbox()
        outbox.enqueue(command("K3", createdAt = T0 + 2.seconds))
        outbox.enqueue(command("K1", createdAt = T0))
        outbox.enqueue(command("K2", createdAt = T0 + 1.seconds))

        assertContentEquals(
            listOf("K1", "K2", "K3"),
            outbox.dispatchable(T0 + 1.hours).map { it.idempotencyKey },
        )
    }

    @Test
    fun an_unknown_key_is_reported_rather_than_silently_ignored() {
        assertIs<OutboxOutcome.Unknown>(outbox().onResponse("nope", 200, null, T0))
        assertIs<OutboxOutcome.Unknown>(outbox().onTransportFailure("nope", T0))
    }
}

/** ADD §7.5.3's curve — 1 s to 60 s, exponential, symmetric ±25 % jitter. */
class OutboxRetryPolicyTest {

    private val policy = OutboxRetryPolicy()

    @Test
    fun the_base_delay_doubles_and_is_capped_at_sixty_seconds() {
        // Jitter off, so the curve itself is what is being asserted.
        val bare = OutboxRetryPolicy(jitterFraction = 0.0)
        val expected = listOf(1, 2, 4, 8, 16, 32, 60, 60, 60)

        expected.forEachIndexed { index, seconds ->
            assertEquals(seconds.seconds, bare.backoffFor(index + 1), "attempt ${index + 1}")
        }
    }

    @Test
    fun jitter_stays_inside_a_symmetric_twenty_five_percent_band() {
        val random = Random(42)
        repeat(500) {
            val delay = policy.backoffFor(attempts = 7, random = random)
            assertTrue(delay >= 45.seconds, "$delay below the band")
            assertTrue(delay <= 75.seconds, "$delay above the band")
        }
    }

    @Test
    fun the_first_delay_is_never_zero_so_a_failing_command_cannot_spin() {
        val random = Random(3)
        repeat(200) { assertTrue(policy.backoffFor(attempts = 1, random = random) > kotlin.time.Duration.ZERO) }
    }

    @Test
    fun only_timeouts_rate_limits_and_server_errors_are_retryable() {
        listOf(408, 425, 429, 500, 502, 503, 504).forEach {
            assertTrue(policy.isRetryable(it), "$it should be retryable")
        }
        listOf(400, 401, 403, 404, 409, 410, 422, 426).forEach {
            assertTrue(!policy.isRetryable(it), "$it should not be retryable")
        }
    }

    @Test
    fun the_curve_matches_the_reconnect_backoff_the_rest_of_the_platform_uses() {
        // ADD §7.5.3 fixes one curve for reconnects and for the outbox drain; C017's
        // ReconnectBackoff is the other implementation of it. Two numbers, one source.
        assertEquals(lk.mageride.shared.util.ReconnectBackoff.MIN_DELAY, policy.initial)
        assertEquals(lk.mageride.shared.util.ReconnectBackoff.MAX_DELAY, policy.max)
        assertEquals(lk.mageride.shared.util.ReconnectBackoff.JITTER_FRACTION, policy.jitterFraction)
    }
}
