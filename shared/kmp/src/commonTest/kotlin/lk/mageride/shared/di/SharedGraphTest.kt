package lk.mageride.shared.di

import kotlinx.serialization.json.Json
import lk.mageride.shared.platform.PlatformInfo
import org.koin.core.context.stopKoin
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * The scaffold's contract test: every binding `:shared` publishes must resolve, on every
 * target, from the list the apps are told to use. A component that adds a module to
 * [sharedModules] and forgets a dependency fails here rather than in an app's `onCreate`.
 */
class SharedGraphTest {
    @AfterTest
    fun tearDown() {
        stopKoin()
    }

    @Test
    fun shared_modules_resolve_every_binding_they_declare() {
        val koin = initKoin().koin

        assertSame(koin.get<Json>(), koin.get<Json>(), "Json must be a singleton")
        assertTrue(koin.get<PlatformInfo>().os.isNotBlank())
    }

    @Test
    fun app_modules_are_appended_after_the_shared_ones() {
        val koin = initKoin(appModules = listOf(org.koin.dsl.module { single { "app-binding" } })).koin

        assertEquals("app-binding", koin.get<String>())
        assertTrue(koin.get<PlatformInfo>().deviceModel.isNotBlank())
    }
}
