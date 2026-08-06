package lk.mageride.passenger.di

import lk.mageride.passenger.BuildConfig
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.models.ClientPlatform

/**
 * Every build-time value the app reads, in one place.
 *
 * **This is the only file in the module that may touch [BuildConfig].** A build flag read at two
 * call sites is a flag that gets overridden at one of them, and [apiBaseUrl] is the one that
 * decides whether a release build talks to production or to a laptop.
 *
 * `build.gradle.kts` sets the fields per build type; the reasoning for each value is there.
 *
 * **There is no MQTT here**, unlike the driver app's equivalent: the passenger never publishes a
 * position, so this app has no broker host, no port and no TLS flag to get wrong. The one
 * real-time origin it needs is [apiBaseUrl] — the SignalR hub is `/hubs/live` on the gateway.
 *
 * @property apiBaseUrl Gateway origin. The app never builds a URL from anything else, and
 *   `LiveHub.PATH` is appended to it for the socket.
 * @property pmTilesUrl The PMTiles archive the map style reads (D2' §0.1 — R2, no Google Maps).
 * @property integrityCloudProjectNumber Play Console cloud project number for D-30 attestation.
 *   `0` means this build cannot attest; see [lk.mageride.shared.platform.PlatformAttestationProvider].
 * @property appVersion `versionName`, sent as `X-App-Version` — the D-31 gate's input, not a
 *   diagnostic.
 * @property debug Whether this is the debug build type.
 */
internal data class PassengerEnvironment(
    val apiBaseUrl: String,
    val pmTilesUrl: String,
    val integrityCloudProjectNumber: Long,
    val appVersion: String,
    val debug: Boolean,
) {

    /**
     * The C013 client configuration.
     *
     * `logLevel` is [ApiLogLevel.NONE] in release and never higher than [ApiLogLevel.HEADERS]
     * anywhere: `BODY` would put OTPs, phone numbers and bearer tokens in logcat, which its own
     * KDoc calls out as never shippable.
     */
    fun apiConfig(): ApiConfig = ApiConfig(
        baseUrl = apiBaseUrl,
        appVersion = appVersion,
        platform = ClientPlatform.ANDROID,
        logLevel = if (debug) ApiLogLevel.HEADERS else ApiLogLevel.NONE,
        userAgent = "MageRidePassenger/$appVersion (Android)",
    )

    internal companion object {

        /** Reads the build type's fields. The one call site is [passengerAppModule]. */
        fun fromBuildConfig(): PassengerEnvironment = PassengerEnvironment(
            apiBaseUrl = BuildConfig.API_BASE_URL,
            pmTilesUrl = BuildConfig.PMTILES_URL,
            integrityCloudProjectNumber = BuildConfig.INTEGRITY_CLOUD_PROJECT,
            appVersion = BuildConfig.VERSION_NAME,
            debug = BuildConfig.DEBUG,
        )
    }
}
