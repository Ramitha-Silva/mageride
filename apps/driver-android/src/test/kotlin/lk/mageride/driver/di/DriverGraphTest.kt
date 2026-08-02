package lk.mageride.driver.di

import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.di.sharedModules
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.mqtt.MqttConfig
import org.koin.core.context.startKoin
import org.koin.core.context.stopKoin
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The five bindings `:shared` leaves to an app, and the two values that are wrong silently.
 *
 * `AuthConfig.app` and `ApiConfig.platform`/`appVersion` are the dangerous ones. Getting the
 * surface wrong "signs the user in as the other surface and revokes the session they actually
 * wanted" (AL-08, and `AuthConfig`'s own KDoc); getting `appVersion` or `platform` wrong makes the
 * gateway's D-31 gate answer `426` on **every** route rather than on one. Neither throws, and
 * neither is visible in a screenshot.
 *
 * Only the Context-free half of the graph is resolved here. `SecureStore`,
 * `PlatformAttestationProvider` and `PlatformDatabaseDriverFactory` all need an Android `Context`
 * and are exercised on a device — see the C067 handoff.
 */
class DriverGraphTest {

    private val release = DriverEnvironment(
        apiBaseUrl = "https://api.mageride.lk",
        mqttHost = "mqtt.mageride.lk",
        mqttPort = MqttConfig.TLS_PORT,
        mqttTls = true,
        pmTilesUrl = "https://tiles.mageride.lk/lk.pmtiles",
        integrityCloudProjectNumber = 0,
        appVersion = "1.0.0",
        debug = false,
    )

    @AfterTest
    fun tearDown() {
        stopKoin()
    }

    @Test
    fun the_session_is_scoped_to_the_driver_surface() {
        val koin = start(release)

        // AL-08 keys one active device PER APP. `PASSENGER` here would revoke the driver's session
        // on the same handset and sign them in as somebody they are not.
        assertEquals(AppSurface.DRIVER, koin.get<AuthConfig>().app)
    }

    @Test
    fun the_client_identifies_itself_the_way_the_d_31_gate_expects() {
        val config = start(release).get<ApiConfig>()

        assertEquals(ClientPlatform.ANDROID, config.platform, "X-Platform")
        assertEquals("1.0.0", config.appVersion, "X-App-Version — the gateway floor is 1.0.0")
        assertEquals("https://api.mageride.lk", config.origin)
    }

    @Test
    fun a_release_build_logs_no_request_detail_and_talks_to_production_over_tls() {
        val koin = start(release)

        // ApiLogLevel.BODY would put OTPs, phone numbers and bearer tokens in logcat; its own
        // KDoc says "never ship this". NONE is the only correct release value.
        assertEquals(ApiLogLevel.NONE, koin.get<ApiConfig>().logLevel)

        val mqtt = koin.get<MqttConfig>()
        assertTrue(mqtt.useTls, "the position plane is TLS outside a local compose stack")
        assertEquals(MqttConfig.TLS_PORT, mqtt.port)
        // ADD §7.1's persistent session — the broker holds QoS-1 messages for a device that drops
        // mid-tunnel, which is the whole reason ingest is MQTT.
        assertFalse(mqtt.cleanStart, "the MQTT session is persistent")
    }

    @Test
    fun a_debug_build_never_logs_bodies_either() {
        val debug = release.copy(debug = true, apiBaseUrl = "http://10.0.2.2:5000")

        assertEquals(ApiLogLevel.HEADERS, start(debug).get<ApiConfig>().logLevel)
    }

    @Test
    fun the_app_opens_the_driver_database_and_not_the_passenger_one() {
        // `mobile_db_schema.md` §0.2 — each app ships its own file and its own table set, and the
        // two never merge even when both apps are installed on one handset.
        assertEquals(MageRideApp.DRIVER, start(release).get<MageRideApp>())
    }

    private fun start(environment: DriverEnvironment) =
        startKoin { modules(sharedModules + driverAppModule(environment)) }.koin
}
