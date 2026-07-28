package lk.mageride.shared.db

import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.db.SqlSchema
import app.cash.sqldelight.driver.jdbc.sqlite.JdbcSqliteDriver
import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C018 Definition of Done, item 4: *schema migration from version N to N+1 is tested*.
 *
 * Version 1 is the on-device schema as of Δ 2026-06-28; version 2 is the Δ 2026-07-05 #2 change
 * set (§8) — AL-47's `qr_claimed_at`, AL-48's unmasked phone columns, and `qr_receipt` on
 * `proof_upload_queue.kind`. The spec prints that change set as a migration, so it *is* one; a
 * handset that installed the earlier build has to arrive at the same schema a fresh install gets.
 *
 * The load-bearing assertion is [a_migrated_passenger_database_is_structurally_identical_to_a_fresh_one]
 * and its driver twin: they compare every table, column and index of a migrated database against a
 * freshly created one. That is what stops the `.sqm` and the `.sq` drifting — without it, a
 * migration can be "correct" for years and produce a schema no query matches.
 */
class SchemaMigrationTest {

    @Test
    fun a_version_one_passenger_database_migrates_and_keeps_its_rows() {
        val driver = openAtV1(PassengerDb.SCHEMA, PASSENGER_V1_DOWNGRADE)

        driver.executeScript(
            """
            INSERT INTO rides (
                id, client_request_id, state, is_active, kind, is_proxy, vehicle_type,
                pickup_lat, pickup_lng, dropoff_lat, dropoff_lng,
                rider_name, rider_phone_masked, driver_name, driver_phone_masked,
                fare_amount_minor, created_at, updated_at
            ) VALUES (
                'R1', 'CRQ1', 'Completed', 0, 1, 1, 'tuk',
                6.9271, 79.8612, 6.8, 79.9,
                'Nimal', '+9477*****67', 'Sunil', '+9471*****23',
                45000, 1000, 2000
            );
            INSERT INTO location_requests (request_id, rider_phone_masked, state, issued_at)
            VALUES ('LR1', '+9477*****67', 'Confirmed', 1500);
            """.trimIndent(),
        )

        PassengerDb.SCHEMA.migrate(driver, 1, PassengerDb.SCHEMA.version)
        driver.setUserVersion(PassengerDb.SCHEMA.version)

        PassengerDb(driver).use { db ->
            val ride = db.sql.ridesQueries.selectById("R1").executeAsOne()
            // AL-48: the masked value is carried across into the renamed column verbatim. The
            // server stops masking; it does not retro-fix what the old build already cached.
            assertEquals("+9477*****67", ride.rider_phone)
            assertEquals("+9471*****23", ride.driver_phone)
            // AL-47: new column, NULL for a ride that predates the QR flow.
            assertNull(ride.qr_claimed_at)
            assertEquals(45_000, ride.fare_amount_minor)
            assertEquals("CRQ1", ride.client_request_id)

            val request = db.sql.locationRequestsQueries.selectById("LR1").executeAsOne()
            assertEquals("+9477*****67", request.rider_phone)
            assertEquals("Confirmed", request.state)
            // The column default survives the rebuild.
            assertEquals(300, request.ttl_seconds)
        }
    }

    @Test
    fun a_version_one_driver_database_migrates_and_keeps_its_rows() {
        val driver = openAtV1(DriverDb.SCHEMA, DRIVER_V1_DOWNGRADE)

        driver.executeScript(
            """
            INSERT INTO active_ride (
                id, state, kind, is_proxy, rider_name, rider_phone_masked,
                pickup_lat, pickup_lng, dropoff_lat, dropoff_lng,
                needs_proof, payment_method, fare_amount_minor, created_at, updated_at
            ) VALUES (
                'R9', 'DriverArrived', 2, 0, 'Kamala', '+9477*****89',
                6.9, 79.8, 6.8, 79.9, 1, 'cod', 62000, 1000, 2000
            );
            INSERT INTO dispatch_offers (
                id, ride_id, vehicle_type, pickup_lat, pickup_lng, dropoff_lat, dropoff_lng,
                rider_phone_masked, status, sent_at, expires_at
            ) VALUES ('O9', 'R9', 'tuk', 6.9, 79.8, 6.8, 79.9, '+9477*****89', 'ACCEPTED', 900, 915);
            INSERT INTO proof_upload_queue (id, ride_id, kind, local_path, captured_at)
            VALUES ('P9', 'R9', 'delivery_photo', '/data/proof/p9.jpg', 3000);
            INSERT INTO credit_transfers (
                id, direction, counterparty_driver_id, counterparty_phone_masked,
                amount_minor, status, created_at
            ) VALUES ('CT9', 'incoming', 'D2', '+9476*****54', 250000, 'APPROVED', 4000);
            """.trimIndent(),
        )

        DriverDb.SCHEMA.migrate(driver, 1, DriverDb.SCHEMA.version)
        driver.setUserVersion(DriverDb.SCHEMA.version)

        DriverDb(driver).use { db ->
            val ride = db.sql.activeRideQueries.selectById("R9").executeAsOne()
            assertEquals("+9477*****89", ride.rider_phone)
            assertNull(ride.qr_claimed_at)
            assertTrue(ride.needs_proof)
            assertEquals(2, ride.kind)

            assertEquals("+9477*****89", db.sql.dispatchOffersQueries.selectById("O9").executeAsOne().rider_phone)
            assertEquals(
                "+9476*****54",
                db.sql.creditTransfersQueries.selectRecent(10).executeAsList().single().counterparty_phone,
            )
            assertEquals(1, db.sql.proofUploadQueueQueries.selectForRide("R9").executeAsList().size)

            // The widened CHECK is live after the migration, not just on a fresh install.
            db.sql.proofUploadQueueQueries.enqueue(
                id = "P10", ride_id = "R9", kind = "qr_receipt", local_path = "/data/proof/qr.png",
                sha256_hex = null, captured_lat = null, captured_lng = null,
                captured_at = NOW, next_retry_at = null,
            )
            assertEquals(2, db.sql.proofUploadQueueQueries.selectForRide("R9").executeAsList().size)
        }
    }

    @Test
    fun a_migrated_passenger_database_is_structurally_identical_to_a_fresh_one() {
        assertMigratedMatchesFresh(PassengerDb.SCHEMA, PASSENGER_V1_DOWNGRADE)
    }

    @Test
    fun a_migrated_driver_database_is_structurally_identical_to_a_fresh_one() {
        assertMigratedMatchesFresh(DriverDb.SCHEMA, DRIVER_V1_DOWNGRADE)
    }

    @Test
    fun the_migration_leaves_no_scaffolding_behind() {
        listOf(
            PassengerDb.SCHEMA to PASSENGER_V1_DOWNGRADE,
            DriverDb.SCHEMA to DRIVER_V1_DOWNGRADE,
        ).forEach { (schema, downgrade) ->
            val driver = openAtV1(schema, downgrade)
            schema.migrate(driver, 1, schema.version)

            // The rebuild renames the old table aside before dropping it. If one of those ever
            // survived, the next migration would find two tables where it expected one.
            val leftovers = driver.tableNames().filter { it.endsWith("_v1") }
            assertTrue(leftovers.isEmpty(), "migration left $leftovers behind")
            driver.close()
        }
    }

    @Test
    fun both_schemas_are_at_version_two() {
        // The version is derived from the migration files present, so this is the one assertion
        // that fails if a `.sqm` is added or removed without the rest of this test being revisited.
        assertEquals(2L, PassengerDb.SCHEMA.version)
        assertEquals(2L, DriverDb.SCHEMA.version)
    }

    @Test
    fun the_old_column_names_are_gone_after_the_migration() {
        val driver = openAtV1(PassengerDb.SCHEMA, PASSENGER_V1_DOWNGRADE)
        PassengerDb.SCHEMA.migrate(driver, 1, PassengerDb.SCHEMA.version)

        assertTrue("rider_phone_masked" !in driver.columnNamesOf("rides"))
        assertTrue("driver_phone_masked" !in driver.columnNamesOf("rides"))
        assertFails { driver.executeScript("SELECT rider_phone_masked FROM rides") }
        driver.close()
    }

    /**
     * Builds a version-1 database.
     *
     * Create the current schema, then drop the tables migration 1 touches back to their old shape.
     * See [PASSENGER_V1_DOWNGRADE] for why that is more honest than writing all thirty-odd tables
     * out twice.
     */
    private fun openAtV1(schema: SqlSchema<QueryResult.Value<Unit>>, downgrade: String): SqlDriver {
        val driver = JdbcSqliteDriver(JdbcSqliteDriver.IN_MEMORY)
        schema.create(driver)
        driver.executeScript(downgrade)
        driver.setUserVersion(1)
        return driver
    }

    private fun assertMigratedMatchesFresh(schema: SqlSchema<QueryResult.Value<Unit>>, downgrade: String) {
        val migrated = openAtV1(schema, downgrade)
        schema.migrate(migrated, 1, schema.version)

        val fresh = JdbcSqliteDriver(JdbcSqliteDriver.IN_MEMORY).also { schema.create(it) }

        assertEquals(fresh.tableNames(), migrated.tableNames(), "table set")
        fresh.tableNames().forEach { table ->
            assertEquals(fresh.columnsOf(table), migrated.columnsOf(table), "columns of $table")
            assertEquals(fresh.indexesOf(table), migrated.indexesOf(table), "indexes of $table")
            assertEquals(fresh.createSqlOf(table), migrated.createSqlOf(table), "DDL of $table")
        }

        migrated.close()
        fresh.close()
    }
}
