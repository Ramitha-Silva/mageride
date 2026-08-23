package lk.mageride.driver.profile

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import lk.mageride.driver.di.DriverDatabase
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.db.driver.CachedDriverProfile
import lk.mageride.shared.db.driver.DriverProfileCache
import kotlin.time.Clock

/**
 * The driver's own identity on disk, and the one call that refills it (Δ MCS-27).
 *
 * **Its own class rather than three more methods on [ProfileRepository], because detekt was right.**
 * That repository is SCR-DA-029's *network* surface — a profile read, a contact read, four writes —
 * and adding a database handle, a gateway origin and an image fetch to it pushed it past the
 * function ceiling and the parameter ceiling at once. Two ceilings at the same moment is a class
 * asking to be two classes.
 *
 * Every field this fills is a cache of something a server owns. Nothing here is authoritative: it
 * decides what a header draws before the reads answer and is replaced by whatever they say.
 */
internal interface DriverProfileStore {

    /** What to draw right now, or an empty row for a driver this handset has not seen. */
    suspend fun cached(driverId: Ulid): CachedDriverProfile

    /** Records what a completed read answered, so the next open has it. */
    suspend fun cacheIdentity(driverId: Ulid, name: String?, level: Int?, registration: String?)

    /** The driver's photograph as bytes — off disk when they are current, from the network when not. */
    suspend fun photo(driverId: Ulid): ByteArray?

    /** D-26: the next driver to sign in on this handset must not see the last one's face. */
    suspend fun forget()
}

/**
 * [DriverProfileStore] over §3.16 and registry-svc.
 *
 * An interface in front of it because a view model's `init` reaches it, and a unit test of that
 * view model has no business opening an encrypted SQLite file to find out what the header drew.
 */
internal class DbDriverProfileStore(
    private val registry: RegistryApi,
    private val database: DriverDatabase,
) : DriverProfileStore {

    override suspend fun cached(driverId: Ulid): CachedDriverProfile = onCache { it.read(driverId.toString()) }

    override suspend fun cacheIdentity(driverId: Ulid, name: String?, level: Int?, registration: String?) {
        onCache { it.writeIdentity(driverId.toString(), name, level, registration, Clock.System.now()) }
    }

    /**
     * The driver's photograph as bytes — off disk when they are current, from the network when not.
     *
     * **Bytes rather than a URL, which is the whole point of §3.16.** The signed link carries an
     * `expires` that changes on every profile read, so a URL-keyed image cache misses every single
     * time and re-downloads a photograph the handset already holds. The link's `v` says *which*
     * photo it is; when that has not changed nothing is fetched and the avatar paints from disk.
     */
    override suspend fun photo(driverId: Ulid): ByteArray? {
        val id = driverId.toString()
        val link = registry.getDriverProfile()?.photoUrl

        val fetched = link?.let { fetch(driverId, id, signedLinkParameters(it)) }

        return fetched ?: onCache { it.read(id) }.photoBytes
    }

    override suspend fun forget() {
        onCache { it.clear() }
    }

    /**
     * The bytes behind one signed link, or `null` for every reason not to go and get them —
     * already current, or a link this build cannot take apart.
     *
     * Null-on-anything rather than branching per case: the caller's answer to all of them is the
     * same, which is to draw whatever is already on disk.
     */
    private suspend fun fetch(driverId: Ulid, id: String, query: Map<String, String>): ByteArray? {
        val version = query["v"]

        if (!onCache { it.needsPhoto(id, version) }) return null

        val expires = query["expires"]?.toLongOrNull() ?: return null
        val signature = query["signature"] ?: return null
        val bytes = registry.getDriverProfilePhoto(driverId, version.orEmpty(), expires, signature)

        onCache { it.writePhoto(id, version, bytes, Clock.System.now()) }

        return bytes
    }

    /**
     * Blocking work off the main thread — SQLDelight's Android driver is synchronous, and these are
     * called from a view model's `init`.
     */
    private suspend fun <T> onCache(block: (DriverProfileCache) -> T): T =
        withContext(Dispatchers.IO) { block(DriverProfileCache(database.get())) }
}

/**
 * The query of a signed link, as a map (Δ MCS-27).
 *
 * `getDriverProfilePhoto` takes `v`, `expires` and `signature` as typed arguments, and the only
 * place they exist is inside the URL the profile read handed back — so something has to take them
 * apart again. Deliberately tolerant: a link missing a parameter yields a map missing a key, and
 * the caller answers that by not fetching rather than by throwing at a driver.
 */
internal fun signedLinkParameters(url: String): Map<String, String> =
    url.substringAfter('?', "")
        .split('&')
        .filter { it.contains('=') }
        .associate { it.substringBefore('=') to it.substringAfter('=') }
