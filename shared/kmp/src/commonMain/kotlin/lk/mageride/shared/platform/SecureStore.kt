package lk.mageride.shared.platform

/**
 * Hardware-backed storage for the handful of strings that must never reach plain settings.
 *
 * Exactly three kinds of value live here (C014): the opaque rotating refresh token, the 30-minute
 * access token, and the MQTT session JWT (E-02). ADD §12.1 names the mechanism per platform —
 * **Android Keystore** and **iOS Keychain + Secure Enclave** — and `mobile_db_schema.md` §0.4
 * puts the tokens here rather than in the SQLite file ("token itself in Keystore"), which is why
 * C018's `auth_session` table stores only expiry timestamps and a `jti`.
 *
 * **Not a settings abstraction.** `SharedPreferences` / `NSUserDefaults` are world-readable on a
 * rooted or jailbroken handset and survive in unencrypted backups; a refresh token there is a
 * session anyone with the file can resume. [PlatformSecureStore] is the only implementation the
 * apps bind, and both actuals encrypt or delegate to the platform keystore — see
 * `AndroidSecureStoreTest` and `PlatformSecureStoreSourceTest`.
 *
 * Implementations must tolerate concurrent calls; [AuthSessionStore] serialises its own writes but
 * the MQTT renewal loop writes on a different coroutine.
 */
public interface SecureStore {

    /** The stored value for [key], or `null` when there is none. */
    public suspend fun read(key: String): String?

    /** Stores [value] under [key], replacing anything already there. */
    public suspend fun write(key: String, value: String)

    /** Removes [key]. Removing an absent key is not an error. */
    public suspend fun delete(key: String)

    /** Removes everything in this store's namespace — logout, revocation and PDPA erasure. */
    public suspend fun clear()
}

/**
 * The platform's hardware-backed [SecureStore]: Android Keystore, iOS Keychain.
 *
 * The constructor differs per platform because the platforms differ — Android needs a `Context`
 * to reach its private preferences file, iOS needs a Keychain service name — so this is an
 * `expect class` with no common constructor and the **app** builds it. C067 / C076 call
 * `PlatformSecureStore(context, namespace)`; C085 / C094 call `PlatformSecureStore(service)`.
 * `commonMain` only ever sees the [SecureStore] interface, which is also what makes the session
 * layer testable without a device.
 *
 * Both actuals are namespaced, so the driver and passenger surfaces cannot read each other's
 * session even when the same handset runs both (AL-08).
 */
public expect class PlatformSecureStore : SecureStore
