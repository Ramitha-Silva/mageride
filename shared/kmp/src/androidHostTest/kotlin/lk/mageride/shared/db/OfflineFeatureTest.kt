package lk.mageride.shared.db

import app.cash.sqldelight.db.SqlDriver
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes

/**
 * The three ADD requirements C018 owns beyond the two durable queues: **D-26** (trilingual offline
 * copy), **D-34** (live-trip share tokens) and **MAP-09** (offline map bundles) — plus §0.5's
 * schema-revision row.
 */
class OfflineFeatureTest {

    private val passenger = openPassenger()
    private val driverDb = openDriverDb()

    @AfterTest
    fun close() {
        passenger.close()
        driverDb.close()
    }

    @Test
    fun a_notification_template_is_only_offline_ready_when_all_three_languages_are_cached() {
        // D-26 is a platform rule, not a nicety: a push that arrives with no link still has to
        // render in the user's language. The CHECK admits exactly si/ta/en and the composite key is
        // (template_key, language), so a partially synced template is detectable rather than a
        // silent English fallback.
        val queries = passenger.sql.contentTemplatesQueries
        queries.upsert("ride.accepted", "en", "Driver on the way", "Your driver is on the way", 1, NOW)
        queries.upsert("ride.accepted", "si", "රියදුරු පැමිණෙමින්", "ඔබේ රියදුරු පැමිණෙමින් සිටී", 1, NOW)

        assertEquals(2, queries.countLanguagesFor("ride.accepted").executeAsOne())

        queries.upsert("ride.accepted", "ta", "ஓட்டுநர் வருகிறார்", "உங்கள் ஓட்டுநர் வருகிறார்", 1, NOW)
        assertEquals(3, queries.countLanguagesFor("ride.accepted").executeAsOne())
        assertEquals(
            "ඔබේ රියදුරු පැමිණෙමින් සිටී",
            queries.select("ride.accepted", "si").executeAsOne().body,
        )
    }

    @Test
    fun a_language_outside_si_ta_en_is_rejected_by_both_cached_content_tables() {
        assertRejected { passenger.sql.contentTemplatesQueries.upsert("k", "fr", null, "body", 1, NOW) }
        assertRejected { passenger.sql.faqArticlesQueries.upsert("f1", "fares", "Title", "Body", "fr", 0, NOW) }
    }

    @Test
    fun faq_articles_come_back_in_the_order_the_help_screen_renders_them() {
        val queries = passenger.sql.faqArticlesQueries
        queries.upsert("f3", "wallet", "Top up", "…", "en", 2, NOW)
        queries.upsert("f1", "fares", "How is my fare calculated", "…", "en", 0, NOW)
        queries.upsert("f2", "wallet", "Vouchers", "…", "en", 1, NOW)
        queries.upsert("f4", "fares", "How is my fare calculated", "…", "si", 0, NOW)

        assertEquals(listOf("f1", "f2", "f3"), queries.selectByLanguage("en").executeAsList().map { it.id })
        assertEquals(listOf("f2", "f3"), queries.selectByCategory("en", "wallet").executeAsList().map { it.id })
    }

    @Test
    fun a_live_trip_share_is_readable_until_it_expires_or_is_revoked() {
        // D-34 / US-12.8. The token here is a PUBLIC share handle for the read-only track view —
        // the one token-shaped column §0.4 allows on the device.
        val queries = passenger.sql.tripSharesQueries
        queries.insert("tok-live", "R1", "https://passenger.mageride.lk/t/tok-live", NOW + 2.hours, false, NOW)
        queries.insert("tok-old", "R1", "https://passenger.mageride.lk/t/tok-old", NOW - 1.hours, false, NOW - 3.hours)

        assertEquals(listOf("tok-live"), queries.selectLiveForRide("R1", NOW).executeAsList().map { it.token })

        queries.revoke("tok-live")
        assertTrue(queries.selectLiveForRide("R1", NOW).executeAsList().isEmpty())
    }

    @Test
    fun an_offline_map_bundle_tracks_its_download_and_its_disk_budget() {
        // MAP-09. The row is metadata; the tiles are a file at `local_path`, which is why §4.3's
        // rule is a size budget rather than a row count.
        val queries = passenger.sql.offlineMapBundlesQueries
        queries.upsert(
            id = "b1", region_name = "Colombo", bbox_json = "[79.8,6.8,80.0,7.0]",
            pmtiles_url = "https://r2/colombo.pmtiles", local_path = null, size_bytes = null,
            state = "QUEUED", downloaded_at = null, expires_at = null,
        )
        assertEquals(0L, queries.totalSizeBytes().executeAsOne())
        assertTrue(queries.selectReady().executeAsList().isEmpty())

        queries.upsert(
            id = "b1", region_name = "Colombo", bbox_json = "[79.8,6.8,80.0,7.0]",
            pmtiles_url = "https://r2/colombo.pmtiles", local_path = "/data/maps/colombo.pmtiles",
            size_bytes = 48_000_000, state = "READY", downloaded_at = NOW, expires_at = NOW + 30.days,
        )

        assertEquals(48_000_000L, queries.totalSizeBytes().executeAsOne())
        assertEquals(listOf("b1"), queries.selectReady().executeAsList().map { it.id })
        assertRejected { queries.updateState("DELETED", "b1") }
    }

    @Test
    fun the_auth_session_row_holds_expiry_hints_and_no_token_at_all() {
        // §0.4 / C014: the tokens are in the Keystore. What SQLite gets is the refresh session id
        // and the two instants that drive proactive refresh (D-29) and MQTT renewal (E-02).
        driverDb.sql.authSessionQueries.upsert(
            user_id = "U1", app = "driver", device_id = "01JZDEVICE", jti = "sess-1",
            access_token_expires_at = NOW + 30.minutes, mqtt_token_expires_at = NOW + 4.hours,
            last_refresh_at = NOW, created_at = NOW, updated_at = NOW,
        )

        val row = driverDb.sql.authSessionQueries.select().executeAsOne()
        assertEquals("sess-1", row.jti)
        assertEquals(NOW + 30.minutes, row.access_token_expires_at)
        assertEquals(NOW + 4.hours, row.mqtt_token_expires_at)

        // AL-08 is per app: the driver file records 'driver' and nothing else is admissible.
        assertRejected { driverDb.sql.authSessionQueries.upsert("U1", "admin", "d", null, null, null, null, NOW, NOW) }
    }

    @Test
    fun the_ui_pref_for_the_call_chooser_stores_only_the_post_AL_48_values() {
        // §6 item 4 as amended by §8: 'normal_masked' was withdrawn with number masking (AL-48).
        val queries = passenger.sql.uiPrefsQueries
        queries.put("last_call_type", "free_voip")
        assertEquals("free_voip", queries.selectRow("last_call_type").executeAsOne().value_)

        queries.put("last_call_type", "direct_dial")
        assertEquals("direct_dial", queries.selectRow("last_call_type").executeAsOne().value_)
    }

    @Test
    fun opening_a_database_records_the_app_schema_revision() = runTest {
        // §0.5: "Room/SQLDelight track PRAGMA user_version; a `meta` KV row ALSO records the app
        // schema rev." user_version stays authoritative — this row is what a support bundle sees.
        val factory = MageRideDatabaseFactory(InMemoryDriverFactory(), keys = null) { NOW }

        factory.openDriver(inMemory = true).use { db ->
            assertEquals(DriverDb.SCHEMA.version, db.meta.getLong(MetaKeys.SCHEMA_REV))
        }
        factory.openPassenger(inMemory = true).use { db ->
            assertEquals(PassengerDb.SCHEMA.version, db.meta.getLong(MetaKeys.SCHEMA_REV))
            assertNull(db.meta.get("nothing-else"))
        }
    }

    private fun assertRejected(block: () -> Unit) {
        assertFails("the constraint did not reject the row") { block() }
    }
}

/** A [DatabaseDriverFactory] over in-memory JDBC SQLite, for exercising the real open path. */
private class InMemoryDriverFactory : DatabaseDriverFactory {
    override fun create(request: DatabaseRequest): SqlDriver =
        openDriverAt(app.cash.sqldelight.driver.jdbc.sqlite.JdbcSqliteDriver.IN_MEMORY, request.schema)

    override fun delete(app: MageRideApp): Boolean = false
}
