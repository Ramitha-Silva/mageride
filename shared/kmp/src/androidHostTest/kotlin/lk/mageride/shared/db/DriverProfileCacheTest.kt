package lk.mageride.shared.db

import lk.mageride.shared.db.driver.DriverProfileCache
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * §3.16's cache, written and read back (Δ MCS-33).
 *
 * **This suite exists because the first write never happened and nothing said so.** MCS-27 wrote
 * `upsertIdentity` as one label over an `UPDATE` and an `INSERT OR IGNORE`; SQLDelight generated
 * the UPDATE, dropped the INSERT, and built clean. The UPDATE matched no row on a handset that had
 * never cached a profile, so the table stayed empty for ever and every read answered
 * [lk.mageride.shared.db.driver.CachedDriverProfile] with nothing in it — which the driver headers
 * correctly drew as *"no name yet"*. Four components tried to fix that from the screen end.
 *
 * So the assertions here are all the same shape on purpose: **write, then read back**. A cache
 * tested only through the type that wraps it, or only for what it returns when empty, would have
 * passed against a table nothing could insert into.
 */
class DriverProfileCacheTest {

    private val driverId = "460a5357-7b8d-4fb8-bf92-901c89f86406"

    private fun cache() = DriverProfileCache(openDriverDb())

    /** The one that was broken: the very first write, against a table with no row for this driver. */
    @Test
    fun `an identity written to an empty table is read back`() {
        val cache = cache()

        cache.writeIdentity(driverId, name = "Ramitha", level = 3, registration = null, at = NOW)

        val cached = cache.read(driverId)

        assertFalse(cached.isEmpty, "the row was inserted, so the read is not an empty profile")
        assertEquals("Ramitha", cached.name)
        assertEquals(3, cached.level)
        assertEquals(NOW, cached.syncedAt)
    }

    /** The second write is the UPDATE half, and it has to reach the row the first one inserted. */
    @Test
    fun `a second identity write updates rather than duplicating`() {
        val cache = cache()

        cache.writeIdentity(driverId, name = "Ramitha", level = 1, registration = null, at = NOW)
        cache.writeIdentity(driverId, name = "Ramitha S", level = 3, registration = "WP CAB-1234", at = NOW)

        val cached = cache.read(driverId)

        assertEquals("Ramitha S", cached.name)
        assertEquals(3, cached.level)
        assertEquals("WP CAB-1234", cached.registration)
    }

    /**
     * `coalesce` is the rule that a read which did not carry a field leaves it alone — the three
     * values arrive from three different calls and one failing must not blank the other two.
     */
    @Test
    fun `a null argument leaves the stored value alone`() {
        val cache = cache()

        cache.writeIdentity(driverId, name = "Ramitha", level = 3, registration = "WP CAB-1234", at = NOW)
        cache.writeIdentity(driverId, name = null, level = null, registration = null, at = NOW)

        val cached = cache.read(driverId)

        assertEquals("Ramitha", cached.name)
        assertEquals(3, cached.level)
        assertEquals("WP CAB-1234", cached.registration)
    }

    /** The photograph has the same two halves and had the same defect. */
    @Test
    fun `a photograph written to an empty table is read back`() {
        val cache = cache()
        val bytes = byteArrayOf(1, 2, 3, 4)

        cache.writePhoto(driverId, version = "ab12cd34", bytes = bytes, at = NOW)

        val cached = cache.read(driverId)

        assertContentEquals(bytes, cached.photoBytes)
        assertEquals("ab12cd34", cached.photoVersion)
    }

    /** An identity refresh must not blank an avatar that is already on screen. */
    @Test
    fun `writing an identity leaves a stored photograph alone`() {
        val cache = cache()
        val bytes = byteArrayOf(9, 8, 7)

        cache.writePhoto(driverId, version = "v1", bytes = bytes, at = NOW)
        cache.writeIdentity(driverId, name = "Ramitha", level = 3, registration = null, at = NOW)

        val cached = cache.read(driverId)

        assertContentEquals(bytes, cached.photoBytes)
        assertEquals("Ramitha", cached.name)
    }

    /** `needsPhoto` is the whole point of storing `v` — and it answered "yes" for ever before. */
    @Test
    fun `a stored photograph at the same version is not fetched again`() {
        val cache = cache()

        assertTrue(cache.needsPhoto(driverId, "v1"), "nothing is stored yet")

        cache.writePhoto(driverId, version = "v1", bytes = byteArrayOf(1), at = NOW)

        assertFalse(cache.needsPhoto(driverId, "v1"))
        assertTrue(cache.needsPhoto(driverId, "v2"), "a different photograph")
    }

    /** D-26: the next driver to sign in on this handset must not see the last one's face. */
    @Test
    fun `clear forgets the driver entirely`() {
        val cache = cache()

        cache.writeIdentity(driverId, name = "Ramitha", level = 3, registration = null, at = NOW)
        cache.writePhoto(driverId, version = "v1", bytes = byteArrayOf(1), at = NOW)

        cache.clear()

        val cached = cache.read(driverId)

        assertTrue(cached.isEmpty)
        assertNull(cached.photoBytes)
    }

    /** A driver this handset has not seen reads as empty rather than throwing. */
    @Test
    fun `an unknown driver reads as an empty profile`() {
        assertTrue(cache().read("someone-else").isEmpty)
    }
}
