package lk.mageride.shared.platform

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * A value that has already been encrypted.
 *
 * The point of the wrapper is that [KeyValueSink] takes **only** this type, so there is no
 * overload through which a plaintext token could reach the preferences file. The DoD line
 * "secrets are never written to plain settings storage" is a type error here rather than a code
 * review.
 *
 * @property encoded Base64 of `IV ‖ ciphertext ‖ GCM tag`.
 */
@JvmInline
internal value class SealedValue(val encoded: String)

/** Where sealed values are parked. The only implementation in production is [SharedPreferencesSink]. */
internal interface KeyValueSink {
    fun read(key: String): SealedValue?

    fun write(key: String, value: SealedValue)

    fun delete(key: String)

    fun clear()
}

/** Seals and opens a value with a key the process cannot extract. */
internal interface PayloadCipher {
    fun seal(plaintext: String): SealedValue

    /** `null` when the value cannot be opened — a rotated key, a restored backup, a corrupt blob. */
    fun unseal(sealed: SealedValue): String?
}

/**
 * The Android half of [SecureStore]: AES-256-GCM under an **Android Keystore** key, parked in a
 * private preferences file.
 *
 * ADD §12.1 names "Android Keystore device binding" for driver sign-in and `mobile_db_schema.md`
 * §0.4 keeps "the token itself in Keystore". The key is generated inside the Keystore and never
 * leaves it — on a device with a TEE or StrongBox the bytes are not in the app's address space at
 * all — so the ciphertext in the preferences file is worthless off the handset, including in an
 * ADB backup or a rooted file copy.
 *
 * **`setUserAuthenticationRequired` is deliberately off.** A driver's handset is locked in a mount
 * for most of a ride, and the MQTT renewal loop (E-02) has to read its credential then. Requiring
 * a screen unlock to decrypt would make the one token designed to survive a long trip the one that
 * cannot be renewed during it.
 *
 * @param context Any context; the application context is what is retained.
 * @param namespace Preferences file name and Keystore alias prefix. C014 passes the surface-scoped
 *   value from [lk.mageride.shared.domain.auth.AuthConfig.storeNamespace], so a handset running
 *   both apps keeps two independent stores (AL-08).
 */
public actual class PlatformSecureStore(context: Context, namespace: String) : SecureStore {

    private val delegate = KeystoreSecureStore(
        sink = SharedPreferencesSink(
            context.applicationContext.getSharedPreferences(namespace, Context.MODE_PRIVATE),
        ),
        cipher = AndroidKeystoreCipher("$namespace.aes"),
    )

    override suspend fun read(key: String): String? = delegate.read(key)

    override suspend fun write(key: String, value: String): Unit = delegate.write(key, value)

    override suspend fun delete(key: String): Unit = delegate.delete(key)

    override suspend fun clear(): Unit = delegate.clear()
}

/**
 * The store's logic, with the platform pushed behind two seams so it can be tested off a device.
 *
 * `AndroidKeyStore` does not exist in a local unit test, which is exactly why the cipher is an
 * interface: `AndroidSecureStoreTest` runs this class against a reversible fake and asserts that
 * what reaches the sink is never the plaintext.
 */
internal class KeystoreSecureStore(
    private val sink: KeyValueSink,
    private val cipher: PayloadCipher,
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
) : SecureStore {

    override suspend fun read(key: String): String? = withContext(dispatcher) {
        sink.read(key)?.let(cipher::unseal)
    }

    override suspend fun write(key: String, value: String) {
        withContext(dispatcher) { sink.write(key, cipher.seal(value)) }
    }

    override suspend fun delete(key: String) {
        withContext(dispatcher) { sink.delete(key) }
    }

    override suspend fun clear() {
        withContext(dispatcher) { sink.clear() }
    }
}

/**
 * Sealed values in a `MODE_PRIVATE` preferences file.
 *
 * **`commit()`, not `apply()`.** The session layer writes the rotated refresh token and only then
 * moves its in-memory copy, because the old token is spent the moment the server answers. An
 * `apply()` that was still queued when the process died would lose the new token and force a
 * sign-out — which is precisely the failure the write ordering exists to prevent.
 */
internal class SharedPreferencesSink(private val preferences: SharedPreferences) : KeyValueSink {

    override fun read(key: String): SealedValue? = preferences.getString(key, null)?.let(::SealedValue)

    override fun write(key: String, value: SealedValue) {
        preferences.edit().putString(key, value.encoded).commit()
    }

    override fun delete(key: String) {
        preferences.edit().remove(key).commit()
    }

    override fun clear() {
        preferences.edit().clear().commit()
    }
}

/**
 * AES-256-GCM with a non-exportable Android Keystore key.
 *
 * The IV is generated by the provider on every encryption (`setRandomizedEncryptionRequired`) and
 * prepended to the ciphertext, so the same token sealed twice produces two different blobs.
 */
internal class AndroidKeystoreCipher(private val keyAlias: String) : PayloadCipher {

    override fun seal(plaintext: String): SealedValue {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        val body = cipher.doFinal(plaintext.encodeToByteArray())
        return SealedValue(Base64.getEncoder().encodeToString(cipher.iv + body))
    }

    override fun unseal(sealed: SealedValue): String? = try {
        val raw = Base64.getDecoder().decode(sealed.encoded)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(TAG_BITS, raw, 0, IV_BYTES))
        cipher.doFinal(raw, IV_BYTES, raw.size - IV_BYTES).decodeToString()
    } catch (_: GeneralSecurityException) {
        // A key that is gone (device reset, app data cleared, backup restored onto another
        // handset) means the session is gone. Answering null puts the user on the login screen,
        // which is recoverable; throwing would make every cold start crash.
        null
    } catch (_: IllegalArgumentException) {
        null
    }

    private fun secretKey(): SecretKey {
        val keystore = KeyStore.getInstance(PROVIDER).apply { load(null) }
        val existing = keystore.getEntry(keyAlias, null) as? KeyStore.SecretKeyEntry
        if (existing != null) return existing.secretKey

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER)
        generator.init(
            KeyGenParameterSpec
                .Builder(keyAlias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(KEY_BITS)
                .setRandomizedEncryptionRequired(true)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val PROVIDER = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val KEY_BITS = 256
        const val TAG_BITS = 128
        const val IV_BYTES = 12
    }
}
