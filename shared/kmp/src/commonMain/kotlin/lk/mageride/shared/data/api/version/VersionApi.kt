package lk.mageride.shared.data.api.version

import io.ktor.client.request.parameter
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.Credential
import lk.mageride.shared.data.api.MageRideApiSignals
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.UpgradeRequiredSignal
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.data.models.version.AppVersionCheck
import kotlin.coroutines.cancellation.CancellationException

/**
 * version-check — the gateway's min-version gate, asked politely (D-31,
 * `backend/contracts/version-check.yaml`).
 *
 * The gate is enforced at the edge on every route: below the floor, *any* call answers
 * `426 upgrade-required`. This endpoint exists so an app can find that out at cold start,
 * before the user has typed anything, and put up the update screen instead of failing the first
 * real request.
 *
 * Public — no credential, and no `X-Attestation`: a build too old to attest still has to be able
 * to learn that it is too old.
 */
public interface VersionApi {

    /**
     * `GET /v1/version/check` — is this build still allowed?
     *
     * @param platform Defaults to the platform in
     *   [lk.mageride.shared.data.api.ApiConfig.platform].
     * @param currentVersion Defaults to
     *   [lk.mageride.shared.data.api.ApiConfig.appVersion].
     * @param publishSignal When `true` and an update is required, also publishes on
     *   [MageRideApiSignals.upgradeRequired], so the cold-start check and a mid-session `426`
     *   reach the app shell through exactly one channel.
     *
     * `@Throws` is load-bearing on this one. Kotlin/Native bridges an exception out of a `suspend`
     * function as an `NSError` **only** when its class is on this list; anything else is "unexpected
     * and unhandled" and terminates the process. This is the cold-start call, it runs before a
     * driver has typed anything, and [MageRideError.Network] is its ordinary answer with no
     * backend reachable — so without this the app dies on launch offline, and no `try?` on the
     * Swift side can catch it (the throw is on a Kotlin worker, not the caller's thread). The same
     * hazard the C091 finding records for non-suspend functions in `IosLiveHubPayloads`.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun checkAppVersion(
        platform: ClientPlatform? = null,
        currentVersion: String? = null,
        publishSignal: Boolean = true,
    ): AppVersionCheck
}

internal class KtorVersionApi(private val transport: ApiTransport, private val signals: MageRideApiSignals) :
    VersionApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun checkAppVersion(
        platform: ClientPlatform?,
        currentVersion: String?,
        publishSignal: Boolean,
    ): AppVersionCheck {
        val result: AppVersionCheck = transport.apiGet(
            service = ApiService.VERSION,
            operationId = "checkAppVersion",
            path = "/v1/version/check",
            credential = Credential.NONE,
        ) {
            parameter("platform", (platform ?: transport.config.platform).wire)
            parameter("current", currentVersion ?: transport.config.appVersion)
        }.decode()

        if (publishSignal && result.updateRequired) {
            signals.publishUpgradeRequired(
                UpgradeRequiredSignal(
                    latestVersion = result.latestVersion,
                    updateUrl = result.updateUrl,
                    isMandatory = result.isMandatory,
                ),
            )
        }
        return result
    }
}
