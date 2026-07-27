package lk.mageride.shared.domain.auth

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.api.UlidIdempotencyKeyGenerator
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.platform.SecureStore
import lk.mageride.shared.serialization.MageRideJson

/**
 * The tokens themselves, as they are written to the secure store.
 *
 * `internal` on purpose: nothing above this layer has a reason to hold a refresh token, and
 * [SessionState.SignedIn] deliberately exposes none. The one way out of the module is
 * [lk.mageride.shared.data.api.TokenProvider.accessToken], which the HTTP pipeline calls.
 *
 * @property userId Owner of the session.
 * @property app The `app` claim AL-08 scopes single-active-device by.
 * @property deviceId Stable per-install id bound to the session.
 * @property accessToken RS256 JWT, 30 minutes (D-29).
 * @property accessTokenExpiresAt When [accessToken] dies, computed from the server's `expiresIn`
 *   at the moment the pair was issued.
 * @property refreshToken Opaque, **single-use**: rotating it invalidates this value (D-29).
 */
@Serializable
internal data class AuthSession(
    val userId: Ulid,
    val app: AppSurface,
    val deviceId: String,
    val accessToken: String,
    val accessTokenExpiresAt: Timestamp,
    val refreshToken: String,
)

/**
 * The MQTT session JWT and what it is bound to (E-02).
 *
 * Public because C017's MQTT client is what presents it to EMQX. It is **not** the API access
 * token and must never be substituted for one: its TTL is `max(active ride + 2 h, 4 h)` and its
 * claims name a vehicle, so a service that accepted it as a bearer credential would be accepting
 * a four-hour token in place of a thirty-minute one.
 *
 * @property jwt The token EMQX validates against its cached JWKS (D-21).
 * @property expiresAt When it dies, from the server's `expiresIn` at issue time.
 * @property vehicleId The vehicle the token authorises publishing for.
 * @property deviceId The publishing device.
 * @property rideId The ride it is bound to, when there is one — this is what extends the TTL past
 *   four hours, so a token minted without one does not cover a long ride.
 */
@Serializable
public data class MqttSessionToken(
    val jwt: String,
    val expiresAt: Timestamp,
    val vehicleId: Ulid,
    val deviceId: String,
    val rideId: Ulid? = null,
) {
    /** Whether this token still covers [vehicleId] and [rideId]. A changed ride needs a new token. */
    internal fun covers(vehicle: Ulid, ride: Ulid?): Boolean = vehicleId == vehicle && rideId == ride
}

/**
 * Everything C014 persists, over [SecureStore].
 *
 * Three values and one identifier: the session token pair, the MQTT session token, and the stable
 * device id. Nothing else — the cached profile, the ride history and the outbox are C018's
 * SQLite database, which is a different security class (`mobile_db_schema.md` §0.4 encrypts the
 * file but keeps "the token itself in Keystore").
 *
 * **The device id outlives a logout.** `mobile_db_schema.md` §0.4 says logout wipes the Keystore
 * entries; [wipeSession] therefore removes both tokens but keeps the device id, because the
 * contract calls it a *per-install* identifier (`iam.yaml`: "Stable per-install device
 * identifier; binds the session") and AL-08's "new device" test is meant to fire when the handset
 * changes, not when the user signs out and back in. [erase] is the PDPA path that takes
 * everything.
 *
 * @property secure Where the values land.
 * @property config Names the keys, so two surfaces on one handset never collide.
 * @property json Wire format for the two records. Always [MageRideJson] outside tests.
 * @property deviceIds Mints the device id on first use. A ULID is 26 characters of the charset the
 *   contract's `maxLength: 128` accepts, and sorts by mint time.
 */
public class AuthSessionStore(
    private val secure: SecureStore,
    private val config: AuthConfig,
    private val json: Json = MageRideJson,
    private val deviceIds: IdempotencyKeyGenerator = UlidIdempotencyKeyGenerator(),
) {
    private val mutex = Mutex()

    /**
     * This install's device id, minted and stored the first time it is asked for.
     *
     * Under a lock because the OTP screen and the MQTT manager can both reach for it at start-up,
     * and two concurrent mints would bind the session to one id and the MQTT token to another.
     */
    public suspend fun deviceId(): String = mutex.withLock {
        val key = config.storeKey(DEVICE_ID)
        secure.read(key) ?: deviceIds.next().also { secure.write(key, it) }
    }

    /** The stored session, or `null` when there is none or the stored form is unreadable. */
    internal suspend fun loadSession(): AuthSession? = load(config.storeKey(SESSION), AuthSession.serializer())

    /** Replaces the stored session. Called before the in-memory copy moves, never after. */
    internal suspend fun saveSession(session: AuthSession) {
        secure.write(config.storeKey(SESSION), json.encodeToString(AuthSession.serializer(), session))
    }

    /** The stored MQTT session token, or `null`. */
    public suspend fun loadMqttToken(): MqttSessionToken? = load(config.storeKey(MQTT), MqttSessionToken.serializer())

    /** Replaces the stored MQTT session token. */
    public suspend fun saveMqttToken(token: MqttSessionToken) {
        secure.write(config.storeKey(MQTT), json.encodeToString(MqttSessionToken.serializer(), token))
    }

    /** Forgets the MQTT session token without touching the API session. */
    public suspend fun clearMqttToken() {
        secure.delete(config.storeKey(MQTT))
    }

    /** Logout, revocation and account switch: both tokens go, the device id stays. */
    public suspend fun wipeSession() {
        secure.delete(config.storeKey(SESSION))
        secure.delete(config.storeKey(MQTT))
    }

    /**
     * PDPA erasure: everything in the namespace, device id included.
     *
     * Pairs with C018 deleting the SQLite file (`mobile_db_schema.md` §0.4). After this the
     * install is indistinguishable from a fresh one.
     */
    public suspend fun erase() {
        secure.clear()
    }

    /**
     * Reads and decodes one record, dropping it if it cannot be read.
     *
     * A stored value that no longer parses is an app update that changed the record's shape. The
     * alternative to deleting it is throwing on every cold start until the user reinstalls, and
     * the recovery from "no session" is a login screen the user already knows how to use.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun <T> load(key: String, serializer: kotlinx.serialization.KSerializer<T>): T? {
        val stored = secure.read(key) ?: return null
        return try {
            json.decodeFromString(serializer, stored)
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            secure.delete(key)
            null
        }
    }

    private companion object {
        const val SESSION = "session"
        const val MQTT = "mqtt"
        const val DEVICE_ID = "device-id"
    }
}
