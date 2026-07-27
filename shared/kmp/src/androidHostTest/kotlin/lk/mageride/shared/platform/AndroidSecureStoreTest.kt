package lk.mageride.shared.platform

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

private const val REFRESH_TOKEN = "opaque-refresh-token-that-must-never-be-readable"

/**
 * The fourth definition-of-done line, Android half: *"secrets are never written to plain settings
 * storage."*
 *
 * `AndroidKeyStore` does not exist in a local JVM unit test, which is why [KeystoreSecureStore]
 * takes its cipher and its sink as interfaces. What is asserted here is the property that has to
 * hold whatever the cipher is: **nothing but sealed bytes reaches the sink**, and a value that
 * cannot be opened reads as "no session" rather than crashing the cold start.
 *
 * The stronger guarantee is structural and needs no test at all — [KeyValueSink] accepts only
 * [SealedValue], so there is no overload through which a plaintext token could be written.
 */
class AndroidSecureStoreTest {

    /** An in-memory sink that also exposes what was actually stored. */
    private class RecordingSink : KeyValueSink {
        val entries: MutableMap<String, String> = mutableMapOf()
        var cleared: Int = 0

        override fun read(key: String): SealedValue? = entries[key]?.let(::SealedValue)

        override fun write(key: String, value: SealedValue) {
            entries[key] = value.encoded
        }

        override fun delete(key: String) {
            entries.remove(key)
        }

        override fun clear() {
            cleared++
            entries.clear()
        }
    }

    /** Reversible, deterministic and obviously not real crypto — the point is the plumbing. */
    private class ReversingCipher(private val openable: Boolean = true) : PayloadCipher {
        override fun seal(plaintext: String): SealedValue = SealedValue("sealed:" + plaintext.reversed())

        override fun unseal(sealed: SealedValue): String? = when {
            !openable -> null
            sealed.encoded.startsWith("sealed:") -> sealed.encoded.removePrefix("sealed:").reversed()
            else -> null
        }
    }

    private fun store(cipher: PayloadCipher, sink: KeyValueSink) =
        KeystoreSecureStore(sink = sink, cipher = cipher, dispatcher = Dispatchers.Unconfined)

    @Test
    fun a_secret_survives_a_round_trip() = runTest {
        val sink = RecordingSink()
        val store = store(ReversingCipher(), sink)

        store.write("session", REFRESH_TOKEN)

        assertEquals(REFRESH_TOKEN, store.read("session"))
    }

    @Test
    fun the_plaintext_never_reaches_the_sink() = runTest {
        val sink = RecordingSink()

        store(ReversingCipher(), sink).write("session", REFRESH_TOKEN)

        assertTrue(sink.entries.isNotEmpty(), "something was stored")
        assertTrue(
            sink.entries.values.none { it.contains(REFRESH_TOKEN) },
            "the preferences file must never hold the token in clear",
        )
    }

    @Test
    fun a_value_that_cannot_be_opened_reads_as_no_session() = runTest {
        // A key that is gone — device reset, cleared app data, a backup restored onto another
        // handset. The recovery is a login screen; throwing would crash every cold start.
        val sink = RecordingSink()
        store(ReversingCipher(), sink).write("session", REFRESH_TOKEN)

        assertNull(store(ReversingCipher(openable = false), sink).read("session"))
    }

    @Test
    fun an_absent_key_reads_as_null_and_deleting_one_is_not_an_error() = runTest {
        val store = store(ReversingCipher(), RecordingSink())

        assertNull(store.read("session"))
        store.delete("session")
    }

    @Test
    fun clearing_empties_the_namespace() = runTest {
        val sink = RecordingSink()
        val store = store(ReversingCipher(), sink)
        store.write("session", REFRESH_TOKEN)
        store.write("mqtt", "mqtt-jwt")

        store.clear()

        assertEquals(1, sink.cleared)
        assertTrue(sink.entries.isEmpty())
        assertFalse(sink.entries.containsKey("session"))
    }
}
