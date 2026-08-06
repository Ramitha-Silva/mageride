package lk.mageride.shared.platform

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * The JVM's [SecureStore]: in memory, for the lifetime of the process, and **not secure**.
 *
 * There is no hardware keystore on a server. The Android actual wraps its values with a key the
 * Android Keystore holds and the iOS one hands them to the Keychain; a JVM has neither, and the
 * options left are a plaintext file or memory. Memory is the honest one — it cannot be mistaken
 * for protection, and it disappears with the process rather than leaving tokens on a build
 * agent's disk.
 *
 * That makes it right for exactly what this target is for: `e2e/walking-skeleton` signs in, holds
 * a session for the length of one scripted ride, and exits. **It is not a store for anything that
 * should outlive a process or that anyone else must not read.** If a later component wants
 * durable JVM-side credentials it should add a real one rather than persist this.
 *
 * [namespace] is honoured for the same reason both device actuals honour it: the driver and
 * passenger surfaces must not be able to read each other's session (AL-08), and the harness runs
 * both in one process — so a shared map would be a bug the apps could never have.
 */
public actual class PlatformSecureStore(private val namespace: String) : SecureStore {

    private val gate = Mutex()
    private val values = mutableMapOf<String, String>()

    /** Namespaced key, so two stores in one process cannot see each other's entries. */
    private fun scoped(key: String): String = "$namespace/$key"

    actual override suspend fun read(key: String): String? = gate.withLock { values[scoped(key)] }

    actual override suspend fun write(key: String, value: String) {
        gate.withLock { values[scoped(key)] = value }
    }

    actual override suspend fun delete(key: String) {
        gate.withLock { values.remove(scoped(key)) }
    }

    actual override suspend fun clear() {
        gate.withLock { values.keys.removeAll { it.startsWith("$namespace/") } }
    }
}
