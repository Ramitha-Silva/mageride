package lk.mageride.shared.db

import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.Instant

/** `mobile_db_schema.md` §4.3 — retention and eviction, and §0.4's full wipe. */
class RetentionAndWipeTest {

    @Test
    fun the_passenger_sweep_applies_every_rule_the_spec_prints() {
        openPassenger().use { db ->
            // command_outbox: ACKED beyond 24 h goes, FAILED stays until the user dismisses it.
            db.outbox.enqueue(command("old-ack", createdAt = NOW - 3.days))
            db.outbox.claim("old-ack", NOW - 3.days)
            db.outbox.onResponse("old-ack", 200, null, NOW - 3.days)
            db.outbox.enqueue(command("failed", createdAt = NOW - 3.days))
            db.outbox.claim("failed", NOW - 3.days)
            db.outbox.onResponse("failed", 422, null, NOW - 3.days)

            // notifications: 30 days or 200 rows.
            db.sql.notificationsQueries.upsert("n-old", "ride_offer", null, null, null, null, false, NOW - 40.days)
            db.sql.notificationsQueries.upsert("n-new", "payment_ok", null, null, null, null, false, NOW)

            // rides: 90 days or 100 rows, and never the live one.
            seedRide(db, "r-old", active = false, createdAt = NOW - 120.days)
            seedRide(db, "r-recent", active = false, createdAt = NOW - 1.days)
            seedRide(db, "r-live", active = true, createdAt = NOW - 200.days)

            // fare_estimates: gone at expires_at.
            db.sql.fareEstimatesQueries.upsert(
                "fe-dead", 6.9, 79.8, 6.8, 79.9, "tuk", 42_000, 0, 3_100, NOW - 1.hours, NOW - 30.minutes,
            )
            db.sql.fareEstimatesQueries.upsert(
                "fe-live", 6.9, 79.8, 6.8, 79.9, "tuk", 42_000, 0, 3_100, NOW, NOW + 5.minutes,
            )

            val report = db.retention.sweep(NOW)

            assertEquals(1, report.removed[RetentionTable.COMMAND_OUTBOX])
            assertEquals(listOf("failed"), db.outbox.failed().map { it.idempotencyKey })

            assertEquals(1, report.removed[RetentionTable.NOTIFICATIONS])
            assertEquals(listOf("n-new"), db.sql.notificationsQueries.selectAll().executeAsList().map { it.id })

            assertEquals(1, report.removed[RetentionTable.RIDES])
            val rides = db.sql.ridesQueries.selectHistory(50, 0).executeAsList().map { it.id }
            assertEquals(listOf("r-recent"), rides)
            assertNotNull(db.sql.ridesQueries.selectActive().executeAsOneOrNull(), "the live ride was evicted")

            assertEquals(1, report.removed[RetentionTable.FARE_ESTIMATES])
            assertEquals(listOf("fe-live"), db.sql.fareEstimatesQueries.selectLive(NOW).executeAsList().map { it.id })
        }
    }

    @Test
    fun the_ride_row_cap_bites_before_the_age_rule_does() {
        openPassenger().use { db ->
            repeat(12) { seedRide(db, "r$it", active = false, createdAt = NOW - it.minutes) }

            db.retention.sweep(NOW, RetentionPolicy(ridesMax = 5))

            val kept = db.sql.ridesQueries.selectHistory(50, 0).executeAsList().map { it.id }
            assertEquals(listOf("r0", "r1", "r2", "r3", "r4"), kept, "the newest five, not an arbitrary five")
        }
    }

    @Test
    fun the_driver_sweep_keeps_a_failed_proof_and_drops_an_uploaded_one() {
        openDriverDb().use { db ->
            listOf("p-uploaded" to "UPLOADED", "p-failed" to "FAILED", "p-pending" to "PENDING")
                .forEach { (id, _) ->
                    db.sql.proofUploadQueueQueries.enqueue(
                        id = id, ride_id = "R1", kind = "delivery_photo", local_path = "/data/$id.jpg",
                        sha256_hex = null, captured_lat = null, captured_lng = null,
                        captured_at = NOW, next_retry_at = null,
                    )
                }
            db.sql.proofUploadQueueQueries.markUploaded("https://r2/p-uploaded.jpg", "p-uploaded")
            db.sql.proofUploadQueueQueries.markFailed("p-failed")

            val report = db.retention.sweep(NOW)

            assertEquals(1, report.removed[RetentionTable.PROOF_UPLOAD_QUEUE])
            // P-10 proof is delivery evidence: a FAILED upload is kept for manual retry (§4.3), and
            // its file must survive with it.
            assertEquals(
                setOf("p-failed", "p-pending"),
                db.sql.proofUploadQueueQueries.selectForRide("R1").executeAsList().map { it.id }.toSet(),
            )
        }
    }

    @Test
    fun an_expired_offer_is_swept_only_after_its_grace() {
        openDriverDb().use { db ->
            db.sql.dispatchOffersQueries.upsert(
                id = "O1", ride_id = "R1", vehicle_type = "tuk",
                pickup_lat = 6.9, pickup_lng = 79.8, pickup_label = null,
                dropoff_lat = 6.8, dropoff_lng = 79.9, dropoff_label = null,
                est_fare_minor = 40_000, distance_to_pickup_m = 800, kind = 0, is_proxy = false,
                rider_name = null, rider_phone = null, package_size = null, package_description = null,
                status = "OFFERED", sent_at = NOW - 1.minutes, expires_at = NOW - 45.seconds,
            )

            db.retention.sweep(NOW, RetentionPolicy(offerGrace = 5.minutes))
            assertEquals(1, db.sql.dispatchOffersQueries.selectById("O1").executeAsList().size)

            db.retention.sweep(NOW + 10.minutes, RetentionPolicy(offerGrace = 5.minutes))
            assertTrue(db.sql.dispatchOffersQueries.selectById("O1").executeAsList().isEmpty())
        }
    }

    @Test
    fun map_bundles_are_reported_for_release_rather_than_silently_dropped() {
        openPassenger().use { db ->
            db.sql.offlineMapBundlesQueries.upsert(
                id = "b-stale", region_name = "Colombo", bbox_json = "[79.8,6.8,80.0,7.0]",
                pmtiles_url = "https://r2/colombo.pmtiles", local_path = "/data/maps/colombo.pmtiles",
                size_bytes = 48_000_000, state = "STALE", downloaded_at = NOW - 40.days, expires_at = null,
            )
            db.sql.offlineMapBundlesQueries.upsert(
                id = "b-ready", region_name = "Kandy", bbox_json = "[80.5,7.2,80.8,7.4]",
                pmtiles_url = "https://r2/kandy.pmtiles", local_path = "/data/maps/kandy.pmtiles",
                size_bytes = 22_000_000, state = "READY", downloaded_at = NOW, expires_at = NOW + 30.days,
            )

            val report = db.retention.sweep(NOW)

            assertEquals(1, report.mapBundlesToRelease.size)
            val release = report.mapBundlesToRelease.single()
            assertEquals("b-stale", release.id)
            assertEquals("/data/maps/colombo.pmtiles", release.localPath)
            // The row is still there: dropping it before the app deletes the PMTiles file would
            // orphan 48 MB on disk with nothing left pointing at it (MAP-09).
            assertEquals(2, db.sql.offlineMapBundlesQueries.selectAll().executeAsList().size)
        }
    }

    @Test
    fun the_sweep_leaves_the_account_caches_alone() {
        openDriverDb().use { db ->
            db.sql.walletQueries.upsert("acct-1", -12_500, "LKR", NOW - 200.days, NOW - 200.days)
            db.sql.driverLevelQueries.upsert(2, 340, 500, NOW - 200.days)
            db.sql.vehiclesQueries.upsert(
                id = "V1", registration_number = "WP-CAB-1234", vehicle_type = "tuk", mode = "C",
                status = "APPROVED", dispatch_state = "ACTIVE", rejection_reason = null,
                driver_name = "Sunil", driver_photo_url = null, vehicle_photo_url = null,
                is_selected = true, synced_at = NOW - 200.days, updated_at = NOW - 200.days,
            )

            db.retention.sweep(NOW)

            // A driver offline for a week still has to see a dashboard. §4.3 sweeps queues and
            // history, never the account's own state.
            assertEquals(-12_500, db.sql.walletQueries.select().executeAsOne().balance_minor)
            assertEquals(2, db.sql.driverLevelQueries.select().executeAsOne().level)
            assertEquals(1, db.sql.vehiclesQueries.selectAll().executeAsList().size)
        }
    }

    @Test
    fun the_wipe_empties_every_table_including_ones_no_sweep_touches() {
        openDriverDb().use { db ->
            db.outbox.enqueue(command("k1"))
            db.gpsBuffer("veh-1").record(6.9, 79.8, NOW, NOW)
            db.meta.put(MetaKeys.CADENCE_INTERVAL_MS, "1000", NOW)
            db.sql.walletQueries.upsert("acct-1", 5_000, "LKR", NOW, NOW)
            db.sql.uiPrefsQueries.put("last_call_type", "free_voip")

            db.wipe()

            // §0.4's logout / AL-08 device-revoke / E-06 erasure path. Driven off sqlite_master, so
            // a table added to one schema and not the other cannot be missed.
            db.sqlDriver.tableNames().forEach { table ->
                assertEquals(0, db.sqlDriver.countRowsForTest(table), "$table still has rows")
            }
        }
    }

    @Test
    fun the_wipe_forgets_the_gps_watermark_so_a_new_account_starts_clean() {
        openDriverDb().use { db ->
            repeat(5) { db.gpsBuffer("veh-1").record(6.9, 79.8, NOW, NOW) }

            db.wipe()

            // The cached GpsBuffer is dropped with the rows: keeping it would hand the next account
            // a counter derived from the previous one's watermark.
            assertEquals(1, db.gpsBuffer("veh-1").record(6.9, 79.8, NOW, NOW).seq)
        }
    }

    @Test
    fun sync_cursors_survive_a_sweep_and_do_not_survive_a_wipe() {
        openPassenger().use { db ->
            db.cursors.advance(SyncCursors.RIDES, "cursor-1", NOW)

            db.retention.sweep(NOW)
            assertEquals("cursor-1", db.cursors.cursor(SyncCursors.RIDES))

            db.wipe()
            assertEquals(null, db.cursors.cursor(SyncCursors.RIDES))
        }
    }

    private fun seedRide(db: PassengerDb, id: String, active: Boolean, createdAt: Instant) {
        db.sql.ridesQueries.upsert(
            id = id, client_request_id = "crq-$id", state = if (active) "Ongoing" else "Completed",
            is_active = active, kind = 0, is_proxy = false, vehicle_type = "tuk",
            pickup_lat = 6.9, pickup_lng = 79.8, pickup_label = null,
            dropoff_lat = 6.8, dropoff_lng = 79.9, dropoff_label = null,
            rider_name = null, rider_phone = null, package_size = null, package_description = null,
            accepted_driver_id = null, driver_name = null, driver_photo_url = null,
            driver_rating = null, driver_phone = null, vehicle_reg = null, vehicle_actual_type = null,
            vehicle_lat = null, vehicle_lng = null, vehicle_heading_deg = null, offer_expires_at = null,
            fare_amount_minor = 42_000, surcharge_minor = 0, tip_amount_minor = 0,
            payment_method = "cash", payment_state = null, qr_claimed_at = null,
            created_at = createdAt, updated_at = createdAt, terminal_at = if (active) null else createdAt,
            server_updated_at = null, synced_at = null,
        )
    }
}

private fun app.cash.sqldelight.db.SqlDriver.countRowsForTest(table: String): Long = executeQuery(
    identifier = null,
    sql = "SELECT COUNT(*) FROM $table",
    mapper = { cursor ->
        app.cash.sqldelight.db.QueryResult.Value(if (cursor.next().value) cursor.getLong(0) ?: 0L else 0L)
    },
    parameters = 0,
).value
