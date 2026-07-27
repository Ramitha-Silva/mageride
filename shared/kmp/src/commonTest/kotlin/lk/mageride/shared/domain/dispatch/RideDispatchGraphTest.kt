package lk.mageride.shared.domain.dispatch

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.testConfig
import lk.mageride.shared.di.initKoin
import org.koin.core.context.stopKoin
import org.koin.dsl.module
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * `rideDispatchModule` in the graph the apps actually start.
 *
 * The binding that matters is [OfferSession] — it is the driver's single offer slot (ADD Appendix
 * B.2 invariant 3), so two of them would be two slots. It resolves out of C013 alone: unlike
 * `authModule`, C015 asks the app for nothing new.
 */
class RideDispatchGraphTest {

    @AfterTest
    fun tearDown() {
        stopKoin()
    }

    private fun appModule() = module {
        single<HttpClientEngine> { MockEngine { respond("{}", HttpStatusCode.OK) } }
        single<ApiConfig> { testConfig() }
    }

    @Test
    fun the_offer_slot_resolves_without_any_c015_specific_app_binding() {
        val koin = initKoin(appModules = listOf(appModule())).koin

        val session = koin.get<OfferSession>()

        assertSame(session, koin.get<OfferSession>(), "two offer slots would be two live offers")
        assertTrue(session.isReadyForNextOffer)
    }
}
