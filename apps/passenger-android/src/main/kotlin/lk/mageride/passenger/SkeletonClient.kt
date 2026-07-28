package lk.mageride.passenger

import io.ktor.client.engine.okhttp.OkHttp
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.MageRideApi
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.data.api.mageRideHttpClient
import lk.mageride.shared.data.models.ClientPlatform

/**
 * The four bindings `:shared` leaves to an app, in one place.
 *
 * C013's rule is that the module supplies everything except the HTTP engine and the `ApiConfig`;
 * C014 adds `AuthConfig` and a `SecureStore`. This shell needs the first two and holds its token in
 * memory rather than in `PlatformSecureStore` — a skeleton that persisted a session would be a
 * skeleton someone had to remember to clear.
 *
 * The real wiring is Koin (`sharedModules`) and belongs to C077.
 */
internal object SkeletonClient {

    /**
     * The gateway, from the emulator's point of view.
     *
     * `10.0.2.2` is the host loopback as seen from an Android emulator, which is where
     * `infra/docker-compose.skeleton.yml` publishes the gateway. On a device, point it at the box.
     */
    var baseUrl: String = "http://10.0.2.2:5000"

    private val tokens = MutableTokenProvider()

    /** The signed-in user, once [MainViewModel] has verified an OTP. */
    var userId: String? = null

    val accessToken: String? get() = tokens.current

    fun signedIn(userId: String, accessToken: String) {
        this.userId = userId
        tokens.current = accessToken
    }

    /** A fresh client against the current [baseUrl]. */
    fun api(): MageRideApi {
        val config = ApiConfig(
            baseUrl = baseUrl,
            // Must match `versionName` in build.gradle.kts, which is what D-31's gate compares.
            appVersion = "1.0.0",
            platform = ClientPlatform.ANDROID,
            userAgent = "mageride-passenger-skeleton/1.0",
        )

        return MageRideApi(
            ApiTransport(
                http = mageRideHttpClient(engine = OkHttp.create(), config = config, tokens = tokens),
                config = config,
            ),
        )
    }
}

/**
 * Holds the access token for the send pipeline.
 *
 * [refresh] answers `false`: rotating a refresh token is C014's `AuthSessionManager`, and a shell
 * that reimplemented D-29's single-use rotation would be a second, wrong implementation of the one
 * thing in the session layer that must not be got wrong.
 */
internal class MutableTokenProvider : TokenProvider {

    private val gate = Mutex()

    @Volatile
    var current: String? = null

    override suspend fun accessToken(): String? = gate.withLock { current }

    override suspend fun refresh(staleAccessToken: String?): Boolean = false

    override suspend fun onAuthenticationLost() {
        gate.withLock { current = null }
    }
}
