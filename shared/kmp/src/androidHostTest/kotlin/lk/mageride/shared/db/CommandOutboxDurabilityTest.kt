package lk.mageride.shared.db

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes

/**
 * The C018 Definition of Done, item 2: *a queued command survives process restart and is replayed
 * exactly once*.
 *
 * A real SQLite file, closed and reopened between the two halves of each test — an in-memory
 * database would prove only that Kotlin objects outlive a method call.
 */
class CommandOutboxDurabilityTest {

    private val file = tempDbFile("outbox-durability")

    private fun reopen() = openDriverDb(file)

    @Test
    fun a_command_queued_before_a_crash_is_still_there_afterwards() {
        val key = "01JZ0000000000000000000001"
        reopen().use { db ->
            db.outbox.enqueue(
                OutboxCommand(
                    idempotencyKey = key,
                    endpoint = "/v1/rides/R1/offer/D1/accept",
                    method = OutboxMethod.POST,
                    command = "ride.accept",
                    requestBody = """{"offerId":"O1"}""",
                    createdAt = NOW,
                    entityType = "ride",
                    entityId = "R1",
                ),
            )
        }

        reopen().use { db ->
            val due = db.outbox.dispatchable(NOW)
            assertEquals(listOf(key), due.map { it.idempotencyKey })
            assertEquals("ride.accept", due.single().command)
            assertEquals("""{"offerId":"O1"}""", due.single().requestBody)
        }
    }

    @Test
    fun a_command_that_was_in_flight_when_the_process_died_is_re_sent_under_the_same_key() {
        val key = "01JZ0000000000000000000002"
        reopen().use { db ->
            db.outbox.enqueue(command(key).copy(entityType = "ride", entityId = "R2"))
            assertNotNull(db.outbox.claim(key, NOW))
            // ---- process is killed here: the request may or may not have reached the server ----
        }

        reopen().use { db ->
            val recovered = db.outbox.recover(NOW + 1.minutes)

            assertEquals(listOf(key), recovered.map { it.idempotencyKey })
            val due = db.outbox.dispatchable(NOW + 1.minutes)
            // The SAME idempotency key: the server replays its stored response out of
            // `rides.command_log` rather than executing the command a second time (R-14,
            // ADD §11.13). That is what makes an unknown outcome safe to retry.
            assertEquals(listOf(key), due.map { it.idempotencyKey })
            assertEquals(1, due.single().attempts, "the pre-crash attempt is still counted")
        }
    }

    @Test
    fun a_command_already_acked_is_never_re_sent_after_a_restart() {
        val key = "01JZ0000000000000000000003"
        reopen().use { db ->
            db.outbox.enqueue(command(key))
            db.outbox.claim(key, NOW)
            db.outbox.onResponse(key, 201, """{"rideId":"R3"}""", NOW)
        }

        reopen().use { db ->
            assertTrue(db.outbox.recover(NOW + 1.minutes).isEmpty())
            assertTrue(db.outbox.dispatchable(NOW + 1.minutes).isEmpty())
            // And the stored response is still readable, so a screen restarted mid-flow can render
            // the server's own answer without going back to the network.
            assertEquals("""{"rideId":"R3"}""", db.outbox.byKey(key)?.responseBody)
        }
    }

    @Test
    fun the_primary_key_is_what_enforces_one_command_per_key_not_the_kotlin_check() {
        val key = "01JZ0000000000000000000004"
        reopen().use { db ->
            db.outbox.enqueue(command(key))

            // A second enqueue under the same key must not add a row...
            db.outbox.enqueue(command(key).copy(requestBody = """{"tampered":true}"""))
            assertEquals(1, db.outbox.dispatchable(NOW).size)

            // ...and going around CommandOutbox straight to the table raises, because the query is
            // a plain INSERT rather than INSERT OR IGNORE.
            val direct = runCatching {
                db.sql.commandOutboxQueries.enqueue(
                    idempotency_key = key,
                    endpoint = "/v1/x",
                    http_method = "POST",
                    command = "x",
                    entity_type = null,
                    entity_id = null,
                    request_body = "{}",
                    request_headers = null,
                    created_at = NOW,
                    next_retry_at = null,
                )
            }
            assertTrue(direct.isFailure, "the primary key did not reject a duplicate")
        }
    }

    @Test
    fun an_invalid_http_method_is_rejected_rather_than_silently_dropped() {
        reopen().use { db ->
            val result = runCatching {
                db.sql.commandOutboxQueries.enqueue(
                    idempotency_key = "01JZ0000000000000000000005",
                    endpoint = "/v1/x",
                    http_method = "TRACE",
                    command = "x",
                    entity_type = null,
                    entity_id = null,
                    request_body = "{}",
                    request_headers = null,
                    created_at = NOW,
                    next_retry_at = null,
                )
            }

            // The CHECK is the point: `INSERT OR IGNORE` would have swallowed this and the command
            // would have vanished with no error anywhere.
            assertTrue(result.isFailure, "the http_method CHECK did not fire")
        }
    }

    @Test
    fun the_optimistic_projection_and_the_outbox_row_commit_or_roll_back_together() {
        // §4.1 step 2: "App writes the local projection optimistically (dirty=1) AND inserts a
        // command_outbox row — in ONE transaction." A crash between the two is what this prevents.
        val key = "01JZ0000000000000000000006"
        reopen().use { db ->
            val attempt = runCatching {
                db.transaction {
                    db.sql.activeRideQueries.upsert(
                        id = "R6", state = "Accepted", kind = 0, is_proxy = false,
                        rider_name = "A", rider_phone = null,
                        pickup_lat = 6.9, pickup_lng = 79.8, pickup_label = null,
                        dropoff_lat = 6.8, dropoff_lng = 79.9, dropoff_label = null,
                        package_size = null, package_description = null,
                        needs_pickup_otp = false, needs_delivery_otp = false, needs_proof = false,
                        payment_method = "cash", payment_state = null, fare_amount_minor = 45_000,
                        surcharge_minor = 0, tip_amount_minor = 0, qr_claimed_at = null,
                        created_at = NOW, updated_at = NOW, server_updated_at = null,
                    )
                    db.outbox.enqueue(command(key))
                    error("the process dies between the two writes")
                }
            }

            assertTrue(attempt.isFailure)
            assertNull(db.sql.activeRideQueries.selectById("R6").executeAsOneOrNull())
            assertTrue(db.outbox.dispatchable(NOW).isEmpty())
        }
    }

    @Test
    fun a_response_status_survives_the_restart_that_a_retry_schedule_depends_on() {
        val key = "01JZ0000000000000000000007"
        reopen().use { db ->
            db.outbox.enqueue(command(key))
            db.outbox.claim(key, NOW)
            val outcome = db.outbox.onResponse(key, 503, null, NOW)
            assertIs<OutboxOutcome.Retrying>(outcome)
        }

        reopen().use { db ->
            // Not due yet — the backoff is on disk, not in a field of a dead object.
            assertTrue(db.outbox.dispatchable(NOW).isEmpty())
            assertEquals(listOf(key), db.outbox.dispatchable(NOW + 1.minutes).map { it.idempotencyKey })
        }
    }
}
