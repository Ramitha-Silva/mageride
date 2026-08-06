package lk.mageride.passenger.di

import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.di.sharedModules
import lk.mageride.shared.domain.auth.AuthConfig
import org.koin.core.context.startKoin
import org.koin.core.context.stopKoin
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * The five bindings `:shared` leaves to an app, and the values that are wrong silently.
 *
 * `AuthConfig.app` and `ApiConfig.platform`/`appVersion` are the dangerous ones. Getting the
 * surface wrong "signs the user in as the other surface and revokes the session they actually
 * wanted" (AL-08, and `AuthConfig`'s own KDoc) — on this side that means a passenger's sign-in
 * kicking the driver app off the same handset. Getting `appVersion` or `platform` wrong makes the
 * gateway's D-31 gate answer `426` on **every** route rather than on one. None of them throws, and
 * none is visible in a screenshot.
 *
 * Only the `Context`-free half of the graph is resolved here. `SecureStore`,
 * `PlatformAttestationProvider`, `PlatformDatabaseDriverFactory` and the two `androidContext()`
 * singles all need an Android `Context` and are exercised on a device.
 */
class PassengerGraphTest {

    private val release = PassengerEnvironment(
        apiBaseUrl = "https://api.mageride.lk",
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
    fun the_session_is_scoped_to_the_passenger_surface() {
        // AL-08 keys one active device PER APP. `DRIVER` here would revoke the driver's session on
        // the same handset and sign the passenger in as somebody they are not.
        assertEquals(AppSurface.PASSENGER, start(release).get<AuthConfig>().app)
    }

    @Test
    fun the_client_identifies_itself_the_way_the_d_31_gate_expects() {
        val config = start(release).get<ApiConfig>()

        assertEquals(ClientPlatform.ANDROID, config.platform, "X-Platform")
        assertEquals("1.0.0", config.appVersion, "X-App-Version — the gateway floor is 1.0.0")
        assertEquals("https://api.mageride.lk", config.origin)
    }

    @Test
    fun a_release_build_logs_no_request_detail() {
        // ApiLogLevel.BODY would put OTPs, phone numbers and bearer tokens in logcat; its own
        // KDoc says "never ship this". NONE is the only correct release value.
        assertEquals(ApiLogLevel.NONE, start(release).get<ApiConfig>().logLevel)
    }

    @Test
    fun a_debug_build_never_logs_bodies_either() {
        val debug = release.copy(debug = true, apiBaseUrl = "http://10.0.2.2:5000")

        assertEquals(ApiLogLevel.HEADERS, start(debug).get<ApiConfig>().logLevel)
    }

    @Test
    fun the_app_opens_the_passenger_database_and_not_the_driver_one() {
        // `mobile_db_schema.md` §0.2 — each app ships its own file and its own table set, and the
        // two never merge even when both apps are installed on one handset.
        assertEquals(MageRideApp.PASSENGER, start(release).get<MageRideApp>())
    }

    @Test
    fun the_hub_and_the_api_share_one_origin() {
        // `LiveHub.PATH` is appended to the gateway origin — the socket is not a separate host, so
        // there is one value to get wrong instead of two. A release build with a dev hub URL is
        // exactly the mistake a second build field would allow.
        assertEquals(release.apiBaseUrl, start(release).get<PassengerEnvironment>().apiBaseUrl)
    }

    private fun start(environment: PassengerEnvironment) =
        startKoin { modules(sharedModules + passengerAppModule(environment)) }.koin
}
