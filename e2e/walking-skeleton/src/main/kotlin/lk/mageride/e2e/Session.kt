package lk.mageride.e2e

import io.ktor.client.engine.okhttp.OkHttp
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.MageRideApi
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.data.api.mageRideHttpClient
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.iam.VerifyOtpRequest

/**
 * One signed-in actor: a passenger or a driver, with their own token and their own
 * [MageRideApi].
 *
 * Two of these run side by side in one process, which is why the token lives in the session rather
 * than in a global — the same reason `PlatformSecureStore` is namespaced (AL-08).
 */
internal class Session(
    val userId: String,
    val phone: String,
    val deviceId: String,
    val api: MageRideApi,
    private val tokens: MutableTokenProvider,
) {
    val accessToken: String get() = tokens.current ?: error("$phone has no access token")

    internal companion object {

        /**
         * Signs in over the ordinary phone-OTP flow (AL-07 — the apps have no other).
         *
         * The account is created by iam-svc on the first successful verify, so a passenger needs no
         * seed; the driver's row exists because `db/seed/skeleton.sql` put a `driver` role on it,
         * which opening the Driver App does not confer (C020 decision 4).
         */
        suspend fun signIn(
            environment: Environment,
            otp: OtpReader,
            phone: String,
            surface: AppSurface,
        ): Session {
            val deviceId = "e2e-${surface.wire}-device"
            val tokens = MutableTokenProvider()
            val api = build(environment, tokens)

            val requested = api.iam.requestOtp(
                RequestOtpRequest(phone = phone, deviceId = deviceId, role = surface),
            )

            val code = otp.await(phone)
            val verified = api.iam.verifyOtp(
                VerifyOtpRequest(authId = requested.authId, otp = code, deviceId = deviceId),
            )

            tokens.current = verified.accessToken

            return Session(
                userId = verified.user.userId,
                phone = phone,
                deviceId = deviceId,
                api = api,
                tokens = tokens,
            )
        }

        private fun build(environment: Environment, tokens: MutableTokenProvider): MageRideApi {
            val config = ApiConfig(
                baseUrl = environment.gatewayUrl,
                // D-31's floor is `1.0.0` in the gateway's own configuration (C008); anything at
                // or above it passes. `ClientPlatform.ANDROID` because that is the surface being
                // stood in for — the header is what the version gate reads, not a claim about
                // this process.
                appVersion = "1.0.0",
                platform = ClientPlatform.ANDROID,
                userAgent = "mageride-walking-skeleton/1.0",
            )

            val http = mageRideHttpClient(engine = OkHttp.create(), config = config, tokens = tokens)

            return MageRideApi(ApiTransport(http = http, config = config))
        }
    }
}

/**
 * The token the send pipeline reads.
 *
 * [refresh] deliberately does nothing and answers `false`: a run that lives under a minute never
 * reaches the 30-minute expiry, and a harness that silently refreshed would hide an
 * authentication bug behind a retry. A `401` here should fail the run.
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
