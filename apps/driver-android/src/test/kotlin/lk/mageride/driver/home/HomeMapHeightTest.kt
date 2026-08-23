package lk.mageride.driver.home

import androidx.compose.ui.unit.dp
import lk.mageride.driver.ui.theme.ControlTokens
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * SCR-DA-010's map height, which is arithmetic rather than a constant and so can be wrong quietly.
 *
 * The dashboard map is drawn at one and a half times the height the wireframe's `flex:1` gives it
 * and the screen scrolls to make room, so the number below decides three things at once: how much
 * map the driver sees, where the offline hint and the recentre FAB land, and how far the sheet sits
 * past the fold. This module has no instrumentation source set — nothing else in the build looks at
 * any of that.
 */
class HomeMapHeightTest {

    private val viewport = 640.dp
    private val sheet = 240.dp

    @Test
    fun the_map_takes_what_the_sheet_leaves() {
        assertEquals(400.dp, homeMapNaturalHeight(viewport, sheet))
    }

    /**
     * Δ MCS-29 — the property the whole fix rests on, and the one the old signature could not
     * express: **the banner stack does not change the map's height.**
     *
     * Every banner is driven by data that arrives after the first frame — the daily fee, the
     * low-balance threshold, the ignition and auto-ended notices — so a map measured against the
     * stack was composed tall against an empty one and shrank as each appeared. A driver on a
     * handset reported exactly that, twice, and the second time it was still true after the
     * measurement feedback loop had been removed.
     *
     * There is no banner argument any more, so this test asserts the only thing left to assert:
     * the answer depends on the viewport and the sheet, both of which are there from the start.
     */
    @Test
    fun the_height_is_settled_before_any_data_arrives() {
        // The frame before the standing read answers, and the frame after five banners appear.
        assertEquals(homeMapNaturalHeight(viewport, sheet), homeMapNaturalHeight(viewport, sheet))
        assertEquals(400.dp, homeMapNaturalHeight(viewport, sheet))
    }

    @Test
    fun a_zero_sheet_is_arithmetic_now_and_not_a_first_frame() {
        assertEquals(viewport, homeMapNaturalHeight(viewport, 0.dp))
    }

    @Test
    fun a_viewport_the_chrome_has_eaten_still_leaves_a_map() {
        // A short handset carrying a Mode A/B journey sheet. CSS flex cannot go negative and simply
        // collapses to zero; Compose measures what it is told, so a remainder of -60 would be a map
        // with no pixels rather than a small one.
        val crushed = homeMapNaturalHeight(viewport = 200.dp, sheet = 240.dp)

        assertEquals(ControlTokens.HomeMapMinimum, crushed)
        assertTrue(crushed > 0.dp, "a map measured to zero or less is a map the driver cannot see")
    }
}
