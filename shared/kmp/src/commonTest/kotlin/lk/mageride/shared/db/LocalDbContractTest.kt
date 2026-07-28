package lk.mageride.shared.db

import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.AppSurface
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes

/** §0.2 — one database file per app, and the two never merge. */
class MageRideAppTest {

    @Test
    fun each_app_names_its_own_file() {
        assertEquals("mageride_passenger.db", MageRideApp.PASSENGER.databaseName)
        assertEquals("mageride_driver.db", MageRideApp.DRIVER.databaseName)
        assertNotEquals(MageRideApp.PASSENGER.databaseName, MageRideApp.DRIVER.databaseName)
    }

    @Test
    fun the_surface_matches_the_AL_08_app_claim() {
        assertEquals(AppSurface.PASSENGER, MageRideApp.PASSENGER.surface)
        assertEquals(AppSurface.DRIVER, MageRideApp.DRIVER.surface)
        assertEquals(MageRideApp.DRIVER, MageRideApp.fromWire("driver"))
        assertNull(MageRideApp.fromWire("admin"))
    }
}

/** §4.2 — cache + reconcile. */
class SyncReconcileTest {

    private val v1 = T0
    private val v2 = T0 + 1.minutes

    @Test
    fun a_dirty_row_with_an_unacked_command_wins_over_a_newer_server_copy() {
        val local = LocalRowState(dirty = true, hasPendingCommand = true, serverUpdatedAt = v1)

        assertEquals(ReconcileDecision.KEEP_LOCAL, reconcile(local, serverUpdatedAt = v2))
    }

    @Test
    fun a_dirty_row_whose_command_has_settled_gives_way_to_the_server() {
        // The self-healing case: the ACK landed, or the command failed, or a crash broke the pair
        // §4.1 writes in one transaction. Pinning the row on stale local data forever is worse.
        val local = LocalRowState(dirty = true, hasPendingCommand = false, serverUpdatedAt = v1)

        assertEquals(ReconcileDecision.APPLY_SERVER, reconcile(local, serverUpdatedAt = v2))
    }

    @Test
    fun a_clean_row_is_last_writer_wins_on_server_updated_at() {
        val local = LocalRowState(dirty = false, hasPendingCommand = false, serverUpdatedAt = v2)

        assertEquals(ReconcileDecision.APPLY_SERVER, reconcile(local, serverUpdatedAt = v2 + 1.minutes))
        assertEquals(ReconcileDecision.KEEP_LOCAL, reconcile(local, serverUpdatedAt = v1))
        // Same version re-fetched: not newer, so nothing churns.
        assertEquals(ReconcileDecision.KEEP_LOCAL, reconcile(local, serverUpdatedAt = v2))
    }

    @Test
    fun a_row_that_has_never_been_reconciled_takes_the_server_copy() {
        val local = LocalRowState(dirty = false, hasPendingCommand = false, serverUpdatedAt = null)

        assertEquals(ReconcileDecision.APPLY_SERVER, reconcile(local, serverUpdatedAt = v1))
    }

    @Test
    fun a_server_row_with_no_timestamp_never_overwrites_anything() {
        val local = LocalRowState(dirty = false, hasPendingCommand = false, serverUpdatedAt = v1)

        assertEquals(ReconcileDecision.KEEP_LOCAL, reconcile(local, serverUpdatedAt = null))
    }
}

/** §1.12 — the KV store, its key vocabulary and the §4.2 cursors. */
class MetaStoreTest {

    @Test
    fun keys_are_spelled_in_one_place() {
        assertEquals("gps.seq.veh-1", MetaKeys.gpsSeq("veh-1"))
        assertEquals("sync.cursor.rides", MetaKeys.syncCursor(SyncCursors.RIDES))
        assertEquals("cadence.intervalMs", MetaKeys.CADENCE_INTERVAL_MS)
        assertEquals("min_app_version", MetaKeys.MIN_APP_VERSION)
    }

    @Test
    fun typed_helpers_round_trip_through_text() {
        val meta = FakeMetaStore()

        meta.putLong("n", 42, T0)
        meta.putInstant("t", T0 + 3.hours, T0)

        assertEquals(42, meta.getLong("n"))
        assertEquals(T0 + 3.hours, meta.getInstant("t"))
        assertNull(meta.getLong("missing"))
        meta.put("junk", "not-a-number", T0)
        assertNull(meta.getLong("junk"))
    }

    @Test
    fun a_cursor_is_stored_echoed_and_reset_without_being_parsed() {
        val meta = FakeMetaStore()
        val cursors = SyncCursors(meta)
        val opaque = "eyJvIjoxMjN9.c2ln"

        assertNull(cursors.cursor(SyncCursors.RIDES))
        cursors.advance(SyncCursors.RIDES, opaque, T0)
        assertEquals(opaque, cursors.cursor(SyncCursors.RIDES))

        cursors.advance(SyncCursors.WALLET, "w1", T0)
        cursors.reset()
        assertNull(cursors.cursor(SyncCursors.RIDES))
        assertNull(cursors.cursor(SyncCursors.WALLET))
    }

    @Test
    fun resetting_cursors_leaves_the_gps_watermark_alone() {
        val meta = FakeMetaStore()
        val cursors = SyncCursors(meta)
        meta.putLong(MetaKeys.gpsSeq("veh-1"), 900, T0)
        cursors.advance(SyncCursors.RIDES, "c1", T0)

        cursors.reset()

        // A full re-sync must not cost the vehicle its sequence — that would rewind `seq`.
        assertEquals(900, meta.getLong(MetaKeys.gpsSeq("veh-1")))
    }
}

/** §0.4 — the database key, and the fence that keeps secrets out of SQLite. */
class DatabaseEncryptionTest {

    @Test
    fun a_key_is_minted_once_and_reused() = runTest {
        val secure = RecordingSecureStore()
        val keys = DatabaseKeyManager(secure)

        val first = keys.passphrase(MageRideApp.DRIVER)
        val second = keys.passphrase(MageRideApp.DRIVER)

        assertEquals(DatabaseKeyManager.KEY_BYTES, first.size)
        assertContentEquals(first.bytes, second.bytes)
        assertEquals(1, secure.values.size)
    }

    @Test
    fun the_two_apps_get_different_keys_on_one_handset() = runTest {
        val secure = RecordingSecureStore()
        val keys = DatabaseKeyManager(secure)

        val passenger = keys.passphrase(MageRideApp.PASSENGER)
        val driver = keys.passphrase(MageRideApp.DRIVER)

        assertFalse(passenger.bytes.contentEquals(driver.bytes))
        assertEquals(2, secure.values.size)
    }

    @Test
    fun an_unreadable_stored_key_is_replaced_rather_than_falling_back_to_no_encryption() = runTest {
        val secure = RecordingSecureStore()
        secure.write("db-key:mageride_driver.db", "!!! not base64 !!!")

        val minted = DatabaseKeyManager(secure).passphrase(MageRideApp.DRIVER)

        assertEquals(DatabaseKeyManager.KEY_BYTES, minted.size)
    }

    @Test
    fun forgetting_the_key_leaves_the_other_app_untouched() = runTest {
        val secure = RecordingSecureStore()
        val keys = DatabaseKeyManager(secure)
        keys.passphrase(MageRideApp.PASSENGER)
        keys.passphrase(MageRideApp.DRIVER)

        keys.forget(MageRideApp.DRIVER)

        assertContentEquals(listOf("db-key:mageride_passenger.db"), secure.values.keys.toList())
    }

    @Test
    fun a_passphrase_never_renders_its_bytes() {
        val passphrase = DatabasePassphrase(ByteArray(32) { 7 })

        val rendered = passphrase.toString()

        assertFalse(rendered.contains("7"), rendered)
        assertTrue(rendered.contains("size=32"), rendered)
    }

    @Test
    fun clearing_a_passphrase_zeroes_it() {
        val passphrase = DatabasePassphrase(ByteArray(32) { 9 })

        passphrase.clear()

        assertTrue(passphrase.bytes.all { it == 0.toByte() })
    }
}

/** §4.3 — the retention numbers, as the spec prints them. */
class RetentionPolicyTest {

    private val policy = RetentionPolicy()

    @Test
    fun the_spec_figures_are_the_defaults() {
        assertEquals(24.hours, policy.outboxAcked)
        assertEquals(30.days, policy.notifications)
        assertEquals(200, policy.notificationsMax)
        assertEquals(90.days, policy.rides)
        assertEquals(100, policy.ridesMax)
        assertEquals(6.hours, policy.gps.maxAge)
    }

    @Test
    fun the_gps_row_cap_is_the_same_number_C017s_in_memory_ring_uses() {
        assertEquals(
            lk.mageride.shared.mqtt.PositionReplayQueue.RING_CAPACITY.toLong(),
            policy.gps.maxRows,
        )
    }

    @Test
    fun every_swept_table_names_a_real_sqlite_table() {
        val names = RetentionTable.entries.map { it.tableName }

        assertEquals(names.distinct(), names)
        assertTrue(names.all { it.matches(Regex("[a-z_]+")) }, names.toString())
    }
}
