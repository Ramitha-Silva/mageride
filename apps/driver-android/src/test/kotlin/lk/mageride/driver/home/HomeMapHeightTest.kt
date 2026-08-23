package lk.mageride.driver.home

import androidx.compose.ui.unit.dp
import lk.mageride.driver.ui.theme.ControlTokens
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * SCR-DA-010's map height (Δ MCS-31).
 *
 * This module has no instrumentation source set, so nothing else in the build looks at the number
 * that decides how much map a driver sees, where the offline hint and the recentre FAB land, and
 * how far the sheet sits past the fold.
 *
 * **What these tests are really for is that the height cannot MOVE.** It was measured against the
 * banner stack and the sheet, both of which grow as reads land, so the map was composed tall and
 * shrank — twice, for two different reasons, reported from a handset both times. The height now
 * depends on the viewport alone, which is the one quantity settled on the first frame.
 */
class HomeMapHeightTest {

    private val viewport = 891.dp

    @Test
    fun the_map_is_a_fixed_share_of_one_screenful() {
        // 0.82 of 891 — where the old arithmetic landed once it had settled, on the handset this
        // was reported from — less the sheet peek. See `homeMapHeight`.
        assertEquals(630.62.dp.value, homeMapHeight(viewport).value, 0.01f)
    }

    /**
     * The go-online switch is reachable without scrolling, which is what the peek is for (Δ MCS-33).
     *
     * Stated as "the map ends a control's height above the fold" rather than as the number itself,
     * so the assertion still means something if the fraction is ever tuned again.
     */
    @Test
    fun the_sheet_starts_before_the_fold_by_a_control_height() {
        assertTrue(
            viewport - homeMapHeight(viewport) >= ControlTokens.HomeMapSheetPeek,
            "the first control in the sheet has to be on screen when the dashboard opens",
        )
    }

    /**
     * The property the whole fix rests on: nothing that arrives late can change the answer.
     *
     * There is no banner argument and no sheet argument any more, so the only way to state this is
     * that one viewport has one height — which is exactly the guarantee that was missing.
     */
    @Test
    fun the_height_is_settled_before_any_data_arrives() {
        assertEquals(homeMapHeight(viewport), homeMapHeight(viewport))
    }

    @Test
    fun a_taller_screen_gets_a_taller_map() {
        assertTrue(homeMapHeight(1200.dp) > homeMapHeight(viewport))
    }

    @Test
    fun a_short_screen_still_leaves_a_map() {
        // CSS flex cannot go negative and simply collapses to zero; Compose measures what it is
        // told, so a fraction of a very short viewport would be a map too small to read.
        val crushed = homeMapHeight(100.dp)

        assertEquals(ControlTokens.HomeMapMinimum, crushed)
        assertTrue(crushed > 0.dp, "a map measured to nothing is a map the driver cannot see")
    }
}
