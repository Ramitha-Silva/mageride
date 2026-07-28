package lk.mageride.shared.db

import lk.mageride.shared.platform.SecureStore
import kotlin.io.encoding.Base64
import kotlin.io.encoding.ExperimentalEncodingApi

/**
 * The SQLCipher passphrase for one database file (§0.4).
 *
 * Wraps the bytes rather than a `String` on purpose: a Kotlin `String` is immutable and interned
 * by the runtime, so a key that has been through one cannot be scrubbed and may sit in the heap
 * until the process dies. [clear] zeroes the array once the connection is open.
 *
 * The value is deliberately not `toString()`-able and is not a `data class` — an accidental
 * `println(request)` on a [DatabaseRequest] must not print the key.
 */
public class DatabasePassphrase(bytes: ByteArray) {

    private val key: ByteArray = bytes.copyOf()

    /** The raw key. Platform driver factories only; do not copy it anywhere that outlives the open. */
    public val bytes: ByteArray get() = key

    /** How long the key is, in bytes. Safe to log. */
    public val size: Int get() = key.size

    /** Zeroes the key in place. Call once the connection is open. */
    public fun clear() {
        key.fill(0)
    }

    /** Never renders the key. */
    override fun toString(): String = "DatabasePassphrase(size=$size)"
}

/**
 * Mints, stores and retrieves the database key — the "key is wrapped by the hardware keystore"
 * half of `mobile_db_schema.md` §0.4.
 *
 * The key itself is 32 random bytes from the platform CSPRNG, minted once per install and kept in
 * [SecureStore] — which is Android Keystore-encrypted preferences and an iOS Keychain item with
 * `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (C014). So the on-disk chain is: SQLite file
 * encrypted by SQLCipher with this key, this key encrypted by hardware. Nothing derives the key
 * from a user secret — there is no user secret to derive it from; MageRide signs in with a phone
 * OTP (AL-07) and there is no password anywhere in the app.
 *
 * **Two surfaces on one handset get two keys.** The store key is namespaced by
 * [MageRideApp.databaseName], and C014's [SecureStore] namespaces on top of that, so the driver
 * app cannot open the passenger app's file even on a rooted device where it can read it.
 *
 * `AfterFirstUnlock`, not `WhenUnlocked`, is what makes this workable mid-ride: the driver app
 * writes GPS to `gps_buffer` from a foreground service with the handset in a pocket, and a key
 * that vanished at lock would take the whole replay buffer with it.
 *
 * @param secure Where the key lives. The same store C014's session tokens use.
 * @param random Injectable for tests; production takes the platform CSPRNG.
 */
public class DatabaseKeyManager(
    private val secure: SecureStore,
    private val random: (Int) -> ByteArray = ::secureRandomBytes,
) {

    /**
     * The key for [app], minting and storing one on first use.
     *
     * Not synchronised: two concurrent first-calls could mint two keys and the second would
     * encrypt a file the first had already created. Open each database once, at start-up, from
     * one place — [MageRideDatabaseFactory] is that place.
     */
    @OptIn(ExperimentalEncodingApi::class)
    public suspend fun passphrase(app: MageRideApp): DatabasePassphrase {
        val storeKey = storeKey(app)
        val existing = secure.read(storeKey)
        if (existing != null) {
            val decoded = runCatching { Base64.decode(existing) }.getOrNull()
            // A key that no longer decodes is unrecoverable: the file it opened is unreadable
            // either way, so minting a fresh one and letting the wipe path drop the file is the
            // only outcome that leaves a usable app. Never silently open unencrypted instead.
            if (decoded != null && decoded.size == KEY_BYTES) return DatabasePassphrase(decoded)
        }
        val minted = random(KEY_BYTES)
        secure.write(storeKey, Base64.encode(minted))
        return DatabasePassphrase(minted)
    }

    /**
     * Forgets the key for [app].
     *
     * Pairs with [DatabaseDriverFactory.delete]: dropping the key makes the file unreadable even
     * if a copy of it survives in a backup, which is the point of E-06 (PDPA erasure) and of
     * AL-08's device revoke. Losing the key without deleting the file is not an error — the file
     * is then unopenable and the next open mints a new key and a new database.
     */
    public suspend fun forget(app: MageRideApp) {
        secure.delete(storeKey(app))
    }

    private fun storeKey(app: MageRideApp): String = "$STORE_PREFIX${app.databaseName}"

    public companion object {
        /** 256-bit key — SQLCipher's default cipher is AES-256. */
        public const val KEY_BYTES: Int = 32

        private const val STORE_PREFIX = "db-key:"
    }
}

/**
 * [size] bytes from the platform's cryptographically secure random source.
 *
 * `java.security.SecureRandom` on Android, `SecRandomCopyBytes` on iOS. Never `kotlin.random`:
 * that is a seeded PRNG and a database key drawn from one is guessable from any other value the
 * same generator produced.
 */
public expect fun secureRandomBytes(size: Int): ByteArray
