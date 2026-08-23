package lk.mageride.shared.db

import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * `mobile_db_schema.md` §1–§3, column for column and index for index.
 *
 * The expected sets below are transcribed from the spec (in its post-Δ shape: §6's
 * `driver_phone`, §7's widened `documents.kind`, §8's `qr_claimed_at` and the AL-48 renames), not
 * derived from the `.sq` files — a test that read the schema it is checking would pass for any
 * schema at all.
 */
class MobileSchemaTest {

    // One instance per test method (JUnit), so these are fresh per test and need no teardown —
    // both are in-memory. Fields rather than nested `use` blocks: every assertion below needs both
    // databases, and nesting them buries the actual check three levels deep.
    private val passenger = openPassenger()
    private val driverDb = openDriverDb()

    @AfterTest
    fun close() {
        passenger.close()
        driverDb.close()
    }

    // ---- §1 Shared tables ------------------------------------------------------------------

    private val sharedTables: Map<String, Set<String>> = mapOf(
        // §1.1
        "auth_session" to setOf(
            "id", "user_id", "app", "device_id", "jti", "access_token_expires_at",
            "mqtt_token_expires_at", "last_refresh_at", "created_at", "updated_at",
        ),
        // §1.2
        "user_profile" to setOf(
            "id", "phone", "email", "role", "first_name", "photo_url", "language",
            "default_payment_method", "notif_prefs_json", "emergency_contact_name",
            "emergency_contact_phone", "dirty", "synced_at", "updated_at",
        ),
        // §1.3
        "emergency_contacts" to setOf("id", "name", "phone", "dirty", "synced_at", "updated_at"),
        // §1.4
        "command_outbox" to setOf(
            "idempotency_key", "endpoint", "http_method", "command", "entity_type", "entity_id",
            "request_body", "request_headers", "state", "attempts", "response_status",
            "response_body", "created_at", "last_attempt_at", "next_retry_at",
        ),
        // §1.5
        "gps_buffer" to setOf(
            "seq", "vehicle_id", "lat", "lng", "accuracy_m", "speed_mps", "heading_deg", "hdop",
            "sat_count", "sample_ts", "source", "state", "created_at",
        ),
        // §1.6
        "notifications" to setOf("id", "type", "title", "body", "data_json", "ride_id", "read", "received_at"),
        // §1.7
        "content_templates" to setOf("template_key", "language", "subject", "body", "version", "synced_at"),
        // §1.8
        "faq_articles" to setOf("id", "category", "title", "body", "language", "sort_order", "synced_at"),
        // §1.9
        "offline_map_bundles" to setOf(
            "id", "region_name", "bbox_json", "pmtiles_url", "local_path", "size_bytes", "state",
            "downloaded_at", "expires_at",
        ),
        // §1.10
        "support_tickets" to setOf(
            "id", "category", "description", "ride_id", "screenshot_url", "status", "admin_response",
            "dirty", "created_at", "synced_at",
        ),
        // §1.11
        "ratings_pending" to setOf(
            "subject_id",
            "subject_kind",
            "ratee_id",
            "direction",
            "prompt_shown",
            "created_at",
        ),
        // §1.12
        "meta" to setOf("key", "value", "updated_at"),
        // §6 Δ 2026-06-28 item 4
        "ui_prefs" to setOf("key", "value"),
    )

    // ---- §2 Passenger tables ---------------------------------------------------------------

    private val passengerTables: Map<String, Set<String>> = mapOf(
        "saved_addresses" to setOf(
            "id", "label", "line1", "line2", "line3", "lat", "lng", "dirty", "synced_at", "updated_at",
        ),
        "place_recents" to setOf("id", "label", "line1", "lat", "lng", "use_count", "last_used_at"),
        "rides" to setOf(
            "id", "client_request_id", "state", "is_active", "kind", "is_proxy", "vehicle_type",
            "pickup_lat", "pickup_lng", "pickup_label", "dropoff_lat", "dropoff_lng", "dropoff_label",
            "rider_name", "rider_phone", "package_size", "package_description", "accepted_driver_id",
            "driver_name", "driver_photo_url", "driver_rating", "driver_phone", "vehicle_reg",
            "vehicle_actual_type", "vehicle_lat", "vehicle_lng", "vehicle_heading_deg",
            "offer_expires_at", "fare_amount_minor", "surcharge_minor", "tip_amount_minor",
            "payment_method", "payment_state", "qr_claimed_at", "created_at", "updated_at",
            "terminal_at", "server_updated_at", "synced_at",
        ),
        "fare_estimates" to setOf(
            "id", "pickup_lat", "pickup_lng", "dropoff_lat", "dropoff_lng", "vehicle_type",
            "estimated_minor", "surcharge_pct", "distance_m", "computed_at", "expires_at",
        ),
        "location_requests" to setOf(
            "request_id", "ride_id", "rider_phone", "state", "issued_at", "ttl_seconds",
            "resolved_lat", "resolved_lng", "resolved_accuracy_m", "resolved_at",
        ),
        "blocked_drivers" to setOf("driver_id", "driver_name", "dirty", "created_at", "synced_at"),
        "trip_shares" to setOf("token", "ride_id", "share_url", "expires_at", "revoked", "created_at"),
    )

    // ---- §3 Driver tables ------------------------------------------------------------------

    private val driverTables: Map<String, Set<String>> = mapOf(
        "vehicles" to setOf(
            "id", "registration_number", "vehicle_type", "mode", "status", "dispatch_state",
            "rejection_reason", "driver_name", "driver_photo_url", "vehicle_photo_url",
            "is_selected", "synced_at", "updated_at",
        ),
        "standby_state" to setOf("id", "state", "active_vehicle_id", "pos_rate_interval_ms", "updated_at"),
        "dispatch_offers" to setOf(
            "id", "ride_id", "vehicle_type", "pickup_lat", "pickup_lng", "pickup_label",
            "dropoff_lat", "dropoff_lng", "dropoff_label", "est_fare_minor", "distance_to_pickup_m",
            "kind", "is_proxy", "rider_name", "rider_phone", "package_size", "package_description",
            "status", "sent_at", "expires_at",
        ),
        "active_ride" to setOf(
            "id", "state", "kind", "is_proxy", "rider_name", "rider_phone", "pickup_lat",
            "pickup_lng", "pickup_label", "dropoff_lat", "dropoff_lng", "dropoff_label",
            "package_size", "package_description", "needs_pickup_otp", "needs_delivery_otp",
            "needs_proof", "payment_method", "payment_state", "fare_amount_minor", "surcharge_minor",
            "tip_amount_minor", "qr_claimed_at", "created_at", "updated_at", "server_updated_at",
        ),
        "ride_history" to setOf(
            "id", "state", "kind", "pickup_label", "dropoff_label", "fare_amount_minor",
            "tip_amount_minor", "payment_method", "completed_at", "synced_at",
        ),
        "proof_upload_queue" to setOf(
            "id", "ride_id", "kind", "local_path", "sha256_hex", "captured_lat", "captured_lng",
            "captured_at", "state", "attempts", "storage_url", "next_retry_at",
        ),
        "wallet" to setOf("id", "account_id", "balance_minor", "currency", "updated_at", "synced_at"),
        "wallet_transactions" to setOf(
            "id",
            "kind",
            "amount_minor",
            "balance_after_minor",
            "description",
            "ts",
            "synced_at",
        ),
        "daily_fee_status" to setOf(
            "fee_date",
            "trips_that_day",
            "first_trip_free_used",
            "fee_charged",
            "amount_minor",
            "updated_at",
            "synced_at",
        ),
        "driver_earnings" to setOf("earn_date", "trips", "gross_minor", "daily_fee_minor", "net_minor", "synced_at"),
        "driver_level" to setOf("id", "level", "rating_points", "level_up_threshold", "synced_at"),
        "directional_filter" to setOf(
            "id", "server_id", "destination_lat", "destination_lng", "label", "set_at", "expires_at",
            "uses_today", "max_uses_per_day", "active", "updated_at",
        ),
        "job_board" to setOf(
            "scheduled_ride_id", "pickup_lat", "pickup_lng", "pickup_label", "dropoff_lat",
            "dropoff_lng", "dropoff_label", "vehicle_type", "pickup_time", "distance_m",
            "intent_submitted", "synced_at",
        ),
        "documents" to setOf("id", "vehicle_id", "kind", "status", "expires_at", "synced_at"),
        "credit_transfers" to setOf(
            "id", "direction", "counterparty_driver_id", "counterparty_name", "counterparty_phone",
            "amount_minor", "status", "created_at", "synced_at",
        ),
        // Δ MCS-27 — §3.16. `photo_bytes` is the avatar itself rather than a URL, because the
        // signed link's `expires` changes on every read and a URL-keyed cache would miss each time.
        "driver_profile" to setOf(
            "driver_id",
            "display_name",
            "level",
            "registration",
            "photo_url",
            "photo_version",
            "photo_bytes",
            "synced_at",
        ),
        // Δ MCS-28 — §3.17. The §0.4 identity-document exception: own documents only, encrypted at
        // rest, bounded lifetime, wiped with the file, never exported.
        "document_images" to setOf(
            "document_id",
            "vehicle_id",
            "kind",
            "side",
            "content_type",
            "bytes",
            "version",
            "cached_at",
            "expires_at",
        ),
    )

    /** Every index the spec prints, and the columns it prints them over. */
    private val expectedIndexes: Map<String, Pair<String, List<String>>> = mapOf(
        "ix_outbox_dispatchable" to ("command_outbox" to listOf("state", "next_retry_at")),
        "ix_gps_replay" to ("gps_buffer" to listOf("vehicle_id", "state", "seq")),
        "ix_notif_unread" to ("notifications" to listOf("read", "received_at")),
        "ix_faq_cat" to ("faq_articles" to listOf("language", "category", "sort_order")),
        "ix_recents_recent" to ("place_recents" to listOf("last_used_at")),
        "ix_prides_active" to ("rides" to listOf("is_active", "updated_at")),
        "ix_prides_history" to ("rides" to listOf("created_at")),
        "ix_offers_live" to ("dispatch_offers" to listOf("status", "expires_at")),
        "ix_dride_hist" to ("ride_history" to listOf("completed_at")),
        "ix_proof_dispatch" to ("proof_upload_queue" to listOf("state", "next_retry_at")),
        "ix_wtx_ts" to ("wallet_transactions" to listOf("ts")),
        "ix_jobboard_time" to ("job_board" to listOf("pickup_time")),
        "ix_docs_expiry" to ("documents" to listOf("expires_at")),
        "ix_credit_transfers_recent" to ("credit_transfers" to listOf("created_at")),
        "ix_document_images_expiry" to ("document_images" to listOf("expires_at")),
        "ix_document_images_vehicle" to ("document_images" to listOf("vehicle_id")),
    )

    @Test
    fun the_passenger_database_is_exactly_the_shared_plus_passenger_tables() {
        assertEquals(
            (sharedTables.keys + passengerTables.keys).sorted(),
            passenger.sqlDriver.tableNames().sorted(),
        )
    }

    @Test
    fun the_driver_database_is_exactly_the_shared_plus_driver_tables() {
        assertEquals(
            (sharedTables.keys + driverTables.keys).sorted(),
            driverDb.sqlDriver.tableNames().sorted(),
        )
    }

    @Test
    fun every_documented_column_exists_on_the_passenger_database() {
        assertColumns(passenger.sqlDriver, sharedTables + passengerTables)
    }

    @Test
    fun every_documented_column_exists_on_the_driver_database() {
        assertColumns(driverDb.sqlDriver, sharedTables + driverTables)
    }

    @Test
    fun every_documented_index_exists_over_the_columns_the_spec_prints() {
        expectedIndexes.forEach { (index, spec) ->
            val (table, columns) = spec
            // A driver-owned table lives only in the driver file; a passenger-owned or shared one
            // is in the passenger file either way.
            val handle = if (table in driverTables.keys) driverDb.sqlDriver else passenger.sqlDriver
            val actual = handle.indexesOf(table)[index]
                ?: fail("$index is missing from $table (indexes: ${handle.indexesOf(table).keys})")
            assertEquals(columns, actual, "$index columns")
        }
    }

    @Test
    fun the_shared_indexes_are_in_both_files_not_just_one() {
        listOf("ix_outbox_dispatchable" to "command_outbox", "ix_gps_replay" to "gps_buffer")
            .forEach { (index, table) ->
                assertTrue(index in passenger.sqlDriver.indexesOf(table), "$index missing from the passenger db")
                assertTrue(index in driverDb.sqlDriver.indexesOf(table), "$index missing from the driver db")
            }
    }

    @Test
    fun the_two_schemas_do_not_merge() {
        // §0.2 and this component's fence: passenger and driver ship separate files with separate
        // table sets, even on a handset that has both apps installed (AL-08 is per app).
        val passengerOnly = passenger.sqlDriver.tableNames().toSet()
        val driverOnly = driverDb.sqlDriver.tableNames().toSet()

        driverTables.keys.forEach { assertTrue(it !in passengerOnly, "$it leaked into the passenger db") }
        passengerTables.keys.forEach { assertTrue(it !in driverOnly, "$it leaked into the driver db") }
        assertEquals(sharedTables.keys, passengerOnly intersect driverOnly)
    }

    @Test
    fun no_table_in_either_database_can_hold_a_token_or_an_otp() {
        // §0.4 and this component's second fence. `auth_session` keeps expiry HINTS, never the
        // token; the package OTPs (P-07) are typed in and POSTed, never stored. The only
        // token-shaped column allowed is `trip_shares.token`, a public share handle that
        // authorises a read-only track view and nothing else (D-34).
        listOf(passenger.sqlDriver, driverDb.sqlDriver)
            .flatMap { handle -> handle.tableNames().map { handle to it } }
            .flatMap { (handle, table) -> handle.columnNamesOf(table).map { table to it } }
            .forEach { (table, column) -> assertSafeColumn(table, column) }
    }

    @Test
    fun a_fresh_database_reports_the_current_schema_version() {
        assertEquals(PassengerDb.SCHEMA.version, passenger.sqlDriver.userVersion())
        assertEquals(DriverDb.SCHEMA.version, driverDb.sqlDriver.userVersion())
    }

    @Test
    fun the_check_constraints_the_spec_prints_are_enforced() {
        val db = driverDb
        // AL-47 widened proof_upload_queue.kind; all four values must be accepted...
        listOf("delivery_photo", "signature", "pickup_photo", "qr_receipt").forEachIndexed { i, kind ->
            db.sql.proofUploadQueueQueries.enqueue(
                id = "p$i",
                ride_id = "R1",
                kind = kind,
                local_path = "/tmp/$i.jpg",
                sha256_hex = null,
                captured_lat = null,
                captured_lng = null,
                captured_at = NOW,
                next_retry_at = null,
            )
        }
        assertEquals(4, db.sql.proofUploadQueueQueries.selectForRide("R1").executeAsList().size)

        // ...and nothing else.
        assertFailsWithSql {
            db.sql.proofUploadQueueQueries.enqueue(
                id = "px", ride_id = "R1", kind = "selfie", local_path = "/tmp/x.jpg",
                sha256_hex = null, captured_lat = null, captured_lng = null,
                captured_at = NOW, next_retry_at = null,
            )
        }

        // §3.14 gained revenue_license (US-2.20 / AL-10) and vehicle_photo in the Δ§7 hygiene fix.
        listOf("driving_license", "registration", "permit", "insurance", "revenue_license", "vehicle_photo")
            .forEachIndexed { i, kind -> db.sql.documentsQueries.upsert("d$i", "V1", kind, "VALID", null, null) }
        assertEquals(6, db.sql.documentsQueries.selectAll().executeAsList().size)

        // §3.11: the level ladder is 1..3 and a driver starts at the top (D5' §11).
        assertFailsWithSql { db.sql.driverLevelQueries.upsert(4, 0, 500, null) }
    }

    @Test
    fun the_singleton_tables_admit_exactly_one_row() {
        driverDb.sql.walletQueries.upsert("acct-1", 5_000, "LKR", NOW, NOW)
        driverDb.sql.walletQueries.upsert("acct-2", 7_500, "LKR", NOW, NOW)

        val rows = driverDb.sql.walletQueries.select().executeAsList()
        assertEquals(1, rows.size)
        assertEquals(7_500, rows.single().balance_minor)
    }

    private fun assertFailsWithSql(block: () -> Unit) {
        assertFails("the constraint did not reject the row") { block() }
    }

    private fun assertColumns(handle: app.cash.sqldelight.db.SqlDriver, expected: Map<String, Set<String>>) {
        expected.forEach { (table, columns) ->
            assertEquals(columns.sorted(), handle.columnNamesOf(table).sorted(), "columns of $table")
        }
    }

    private fun assertSafeColumn(table: String, column: String) {
        if (table == "trip_shares" && column == "token") return
        val lower = column.lowercase()
        if ("token" in lower) {
            assertTrue(lower.endsWith("_expires_at"), "$table.$column looks like a stored token (§0.4)")
        }
        if ("otp" in lower) {
            assertTrue(lower.startsWith("needs_"), "$table.$column looks like a stored OTP (§3.4)")
        }
        listOf("password", "secret", "passphrase", "private_key", "refresh_token", "jwt").forEach { banned ->
            assertTrue(banned !in lower, "$table.$column looks like a credential (§0.4)")
        }
    }
}
