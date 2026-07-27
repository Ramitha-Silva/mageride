package lk.mageride.shared.domain.auth

import io.ktor.client.engine.mock.MockRequestHandleScope
import io.ktor.client.request.HttpRequestData
import io.ktor.client.request.HttpResponseData
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.api.MageRideApi
import lk.mageride.shared.data.api.RecordedRequest
import lk.mageride.shared.data.api.TestApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.testApi
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.platform.SecureStore
import lk.mageride.shared.serialization.MageRideJson
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

// Harness for the C014 tests.
//
// Everything here wires the *real* pieces together — the real SessionTokenProvider inside the real
// C013 send pipeline, talking to the real KtorIamApi over a MockEngine. The definition of done is
// about how those interact ("a 401 triggers exactly one refresh attempt and replays the original
// request once"), and a test against a fake pipeline would assert the fake.

/** The instant virtual time starts at, so `expiresAt` values in assertions are readable. */
@OptIn(ExperimentalTime::class)
internal val TEST_EPOCH: Instant = Instant.parse("2026-07-27T00:00:00Z")

internal const val TEST_DEVICE_ID: String = "device-under-test"
internal const val TEST_USER_ID: Ulid = "01JUSERTESTTESTTESTTESTTES"

/** A [SecureStore] that keeps everything in a map, and counts what was asked of it. */
internal class FakeSecureStore : SecureStore {
    val values: MutableMap<String, String> = mutableMapOf()
    var clears: Int = 0
        private set

    override suspend fun read(key: String): String? = values[key]

    override suspend fun write(key: String, value: String) {
        values[key] = value
    }

    override suspend fun delete(key: String) {
        values.remove(key)
    }

    override suspend fun clear() {
        clears++
        values.clear()
    }
}

/** Device ids a test can predict. */
internal class FixedDeviceIds(private val value: String = TEST_DEVICE_ID) : IdempotencyKeyGenerator {
    override fun next(): String = value
}

/** The pieces one test needs, all sharing one [FakeSecureStore] and one MockEngine. */
internal class AuthHarness(
    val config: AuthConfig,
    val secure: FakeSecureStore,
    val store: AuthSessionStore,
    val sessions: AuthSessionManager,
    private val test: TestApi,
    val clock: () -> Timestamp,
) {
    /** The sixteen typed clients, over the MockEngine. */
    val api: MageRideApi get() = test.api

    /** Every request the engine saw, in order. */
    val requests: List<RecordedRequest> get() = test.requests

    /** Requests the engine saw for [path], in order. */
    fun requestsTo(path: String): List<RecordedRequest> = requests.filter { it.path == path }

    /**
     * Puts a signed-in session in the store and restores it, without an HTTP round trip.
     *
     * Most tests are about what happens *after* sign-in; going through the OTP pair every time
     * would add two requests to every assertion about how many requests were made.
     */
    suspend fun signIn(
        accessToken: String = "access-1",
        refreshToken: String = "refresh-1",
        ttl: Duration = 30.minutes,
    ) {
        store.saveSession(
            AuthSession(
                userId = TEST_USER_ID,
                app = config.app,
                deviceId = TEST_DEVICE_ID,
                accessToken = accessToken,
                accessTokenExpiresAt = clock() + ttl,
                refreshToken = refreshToken,
            ),
        )
        secure.values[config.storeKey("device-id")] = TEST_DEVICE_ID
        sessions.restore()
    }

    /** Whether a session record is still in the secure store. */
    fun hasStoredSession(): Boolean = secure.values.containsKey(config.storeKey("session"))

    /** Whether an MQTT token is still in the secure store. */
    fun hasStoredMqttToken(): Boolean = secure.values.containsKey(config.storeKey("mqtt"))

    /**
     * The E-02 token manager, running its renewal loop in [scope].
     *
     * Pass `backgroundScope`: the loop never completes on its own, and `runTest` cancels the
     * background scope for you at the end of the test.
     *
     * @param iam Override the client for the timing tests, which need `issueMqttToken` to answer
     *   on the virtual scheduler rather than on Ktor's own dispatcher.
     */
    fun mqtt(scope: CoroutineScope, iam: IamApi = api.iam): MqttSessionTokenManager = MqttSessionTokenManager(
        api = { iam },
        sessions = sessions,
        store = store,
        config = config,
        clock = clock,
        scope = scope,
    )
}

/**
 * Builds a harness whose engine answers with [respond].
 *
 * The clock is virtual time: `TEST_EPOCH + testScheduler.currentTime`, so `advanceTimeBy` moves
 * both the coroutine clock the renewal loop sleeps on and the wall clock token expiry is measured
 * against. Two clocks that can disagree would make every expiry test a coin toss.
 */
@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
internal fun TestScope.authHarness(
    config: AuthConfig = AuthConfig(app = AppSurface.DRIVER),
    respond: suspend MockRequestHandleScope.(Int, HttpRequestData) -> HttpResponseData,
): AuthHarness {
    val secure = FakeSecureStore()
    val store = AuthSessionStore(
        secure = secure,
        config = config,
        json = MageRideJson,
        deviceIds = FixedDeviceIds(),
    )
    val clock: () -> Timestamp = { TEST_EPOCH + testScheduler.currentTime.milliseconds }

    // The knot C013 and C014 tie together: IamApi needs an HttpClient, the HttpClient needs a
    // TokenProvider, the TokenProvider needs the manager, the manager needs IamApi. Production
    // breaks it with a Koin lookup deferred to first use; a test breaks it with a holder.
    var built: MageRideApi? = null
    val sessions = AuthSessionManager(
        api = { requireNotNull(built) { "the harness was used before it finished building" }.iam },
        store = store,
        config = config,
        clock = clock,
    )
    val test = testApi(tokens = SessionTokenProvider(sessions), respond = respond)
    built = test.api

    return AuthHarness(
        config = config,
        secure = secure,
        store = store,
        sessions = sessions,
        test = test,
        clock = clock,
    )
}

/** A `TokenPair` body, as `POST /v1/auth/refresh` answers it. */
internal fun tokenPairJson(access: String, refresh: String, expiresIn: Int = 1800): String =
    """{"accessToken":"$access","refreshToken":"$refresh","expiresIn":$expiresIn}"""

/** The `POST /v1/auth/otp/verify` body: `allOf(TokenPair, { user, isNewUser })`. */
internal fun verifyOtpJson(
    access: String = "access-1",
    refresh: String = "refresh-1",
    userId: Ulid = TEST_USER_ID,
    isNewUser: Boolean = false,
): String = """
    {"accessToken":"$access","refreshToken":"$refresh","expiresIn":1800,
     "user":{"userId":"$userId","phone":"+94771234567","role":"driver"},
     "isNewUser":$isNewUser}
""".trimIndent()

/** The `POST /v1/auth/mqtt-token` body (E-02). */
internal fun mqttTokenJson(jwt: String, expiresIn: Int = 14400): String =
    """{"mqttJwt":"$jwt","expiresIn":$expiresIn}"""
