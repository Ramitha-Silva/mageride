package lk.mageride.driver.di

import lk.mageride.driver.BuildConfig
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.mqtt.MqttConfig

/**
 * Every build-time value the app reads, in one place.
 *
 * **This is the only file in the module that may touch [BuildConfig].** A build flag read at two
 * call sites is a flag that gets overridden at one of them, and the two that matter here — the
 * gateway origin and the MQTT host — are the pair that decides whether a release build talks to
 * production or to a laptop.
 *
 * `build.gradle.kts` sets the fields per build type; the reasoning for each value is there.
 *
 * @property apiBaseUrl Gateway origin. The app never builds a URL from anything else.
 * @property mqttHost EMQX host for the position plane (D6' §3).
 * @property mqttPort 1883 plain in debug, 8883 TLS in release.
 * @property mqttTls Never false outside a local compose stack.
 * @property pmTilesUrl The PMTiles archive the map style reads (D2' §0.1 — R2, no Google Maps).
 * @property integrityCloudProjectNumber Play Console cloud project number for D-30 attestation.
 *   `0` means this build cannot attest; see [lk.mageride.shared.platform.PlatformAttestationProvider].
 * @property appVersion `versionName`, sent as `X-App-Version` — the D-31 gate's input, not a
 *   diagnostic.
 * @property debug Whether this is the debug build type.
 */
internal data class DriverEnvironment(
    val apiBaseUrl: String,
    val mqttHost: String,
    val mqttPort: Int,
    val mqttTls: Boolean,
    val pmTilesUrl: String,
    val integrityCloudProjectNumber: Long,
    val appVersion: String,
    val debug: Boolean,
) {

    /**
     * The C013 client configuration.
     *
     * `logLevel` is [ApiLogLevel.NONE] in release and never higher than
     * [ApiLogLevel.HEADERS] anywhere: `BODY` would put OTPs, phone numbers and bearer tokens in
     * logcat, which its own KDoc calls out as never shippable.
     */
    fun apiConfig(): ApiConfig = ApiConfig(
        baseUrl = apiBaseUrl,
        appVersion = appVersion,
        platform = ClientPlatform.ANDROID,
        logLevel = if (debug) ApiLogLevel.HEADERS else ApiLogLevel.NONE,
        userAgent = "MageRideDriver/$appVersion (Android)",
    )

    /**
     * The MQTT plane configuration.
     *
     * Everything except host/port/TLS is left at the `MqttConfig` default, because those defaults
     * *are* the spec: a persistent session (`cleanStart = false`, ADD §7.1), a 60 s keepalive and
     * a one-hour session expiry.
     */
    fun mqttConfig(): MqttConfig = MqttConfig(host = mqttHost, port = mqttPort, useTls = mqttTls)

    internal companion object {

        /** Reads the build type's fields. The one call site is [driverAppModule]. */
        fun fromBuildConfig(): DriverEnvironment = DriverEnvironment(
            apiBaseUrl = BuildConfig.API_BASE_URL,
            mqttHost = BuildConfig.MQTT_HOST,
            mqttPort = BuildConfig.MQTT_PORT,
            mqttTls = BuildConfig.MQTT_TLS,
            pmTilesUrl = BuildConfig.PMTILES_URL,
            integrityCloudProjectNumber = BuildConfig.INTEGRITY_CLOUD_PROJECT,
            appVersion = BuildConfig.VERSION_NAME,
            debug = BuildConfig.DEBUG,
        )
    }
}
