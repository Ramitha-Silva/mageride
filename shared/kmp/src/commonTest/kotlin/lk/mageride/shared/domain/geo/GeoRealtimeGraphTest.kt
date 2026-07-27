package lk.mageride.shared.domain.geo

import lk.mageride.shared.di.initKoin
import lk.mageride.shared.di.sharedModules
import org.koin.core.context.stopKoin
import org.koin.dsl.module
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * C017's slice of the Koin graph: one binding, overridable by an app.
 *
 * The override path is not a convenience — it is how iOS gets an H3 engine at all until a
 * `cinterop` binding exists (see `PlatformH3Grid.ios.kt`). Asserting it here, on every target,
 * is what keeps that route working.
 */
class GeoRealtimeGraphTest {

    @AfterTest
    fun tearDown() {
        stopKoin()
    }

    @Test
    fun the_module_is_registered_with_the_shared_graph() {
        assertTrue(geoRealtimeModule in sharedModules, "the apps use sharedModules and nothing else")
    }

    @Test
    fun an_app_supplied_grid_overrides_the_platform_default() {
        val koin = initKoin(appModules = listOf(module { single<H3Grid> { TestH3Grid() } })).koin

        val grid = koin.get<H3Grid>()

        assertIs<TestH3Grid>(grid, "app modules come after sharedModules, so an app binding wins")
        assertEquals(19, GeoCells.viewCells(grid, COLOMBO_FORT).size)
    }

    @Test
    fun every_other_rule_this_component_owns_is_built_at_the_call_site() {
        // Same reasoning as C016's empty module: an AdaptiveRateConfig or an MqttConfig held in the
        // container would pin whatever the operator's numbers were when the app launched.
        val grid = TestH3Grid()

        assertEquals(19, GeoCellSubscription(grid).onPosition(COLOMBO_FORT, GEO_EPOCH).join.size)
        assertEquals(5, GeoCells.dispatchCell(grid, COLOMBO_FORT).resolution)
    }
}
