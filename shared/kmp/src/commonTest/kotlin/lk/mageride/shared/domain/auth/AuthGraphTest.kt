package lk.mageride.shared.domain.auth

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.data.api.testConfig
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.di.initKoin
import lk.mageride.shared.platform.SecureStore
import org.koin.core.context.stopKoin
import org.koin.dsl.module
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertIs
import kotlin.test.assertSame

/**
 * `authModule` in the graph the apps actually start.
 *
 * The binding that matters is [TokenProvider]: C013 declares
 * [lk.mageride.shared.data.api.TokenProvider.Anonymous] as a placeholder and C014 replaces it.
 * Koin's later definition wins and `sharedModules` lists `apiModule` first — if that order ever
 * flips, every request in all four apps goes out unauthenticated, and this is where it is caught.
 */
class AuthGraphTest {

    @AfterTest
    fun tearDown() {
        stopKoin()
    }

    private fun appModule() = module {
        single<HttpClientEngine> { MockEngine { respond("{}", HttpStatusCode.OK) } }
        single<ApiConfig> { testConfig() }
        single { AuthConfig(app = AppSurface.DRIVER) }
        single<SecureStore> { FakeSecureStore() }
    }

    @Test
    fun the_session_backed_token_provider_replaces_the_anonymous_placeholder() {
        val koin = initKoin(appModules = listOf(appModule())).koin

        assertIs<SessionTokenProvider>(koin.get<TokenProvider>())
    }

    @Test
    fun every_c014_binding_resolves_and_is_a_singleton() {
        val koin = initKoin(appModules = listOf(appModule())).koin

        assertSame(koin.get<AuthSessionManager>(), koin.get<AuthSessionManager>())
        assertSame(koin.get<AuthSessionStore>(), koin.get<AuthSessionStore>())
        assertSame(koin.get<MqttSessionTokenManager>(), koin.get<MqttSessionTokenManager>())
        koin.get<MqttSessionTokenManager>().close()
    }
}
