package lk.mageride.driver.home

import androidx.compose.ui.unit.dp
import lk.mageride.driver.ui.theme.ControlTokens
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * SCR-DA-010's map height, which is arithmetic rather than a constant and so can be wrong quietly.
 *
 * The dashboard map is drawn at twice the height the wireframe's `flex:1` gives it and the screen
 * scrolls to make room, so the number below decides three things at once: how much map the driver
 * sees, where the offline hint and the recentre FAB land, and how far the sheet sits past the fold.
 * This module has no instrumentation source set — nothing else in the build looks at any of that.
 */
class HomeMapHeightTest {

    private val viewport = 640.dp
    private val banners = 40.dp
    private val sheet = 240.dp

    @Test
    fun the_map_takes_what_the_banners_and_the_sheet_leave() {
        // The wireframe's `.map{flex:1}`, transcribed: 640 - 40 - 240.
        assertEquals(360.dp, homeMapNaturalHeight(viewport, banners, sheet))
    }

    @Test
    fun a_bannerless_dashboard_gives_the_map_the_banners_share() {
        // Nothing is owed a banner: no daily fee, no low balance, no error. The map absorbs it,
        // which is what `flex:1` does and what the weight-based layout this replaced did.
        assertEquals(400.dp, homeMapNaturalHeight(viewport, 0.dp, sheet))
    }

    @Test
    fun an_unmeasured_sheet_is_the_first_layout_pass_and_not_an_empty_sheet() {
        // `DashboardSheet` always draws a grab handle and its own padding, so a measured sheet is
        // never 0. Reading a zero as a real height would hand the map `viewport - banners` and
        // then double THAT, which is a map two screens tall for the one frame before the sheet
        // reports. The map takes the plain viewport instead.
        assertEquals(viewport, homeMapNaturalHeight(viewport, banners, 0.dp))
        assertEquals(viewport, homeMapNaturalHeight(viewport, banners, (-1).dp))
    }

    @Test
    fun a_viewport_the_chrome_has_eaten_still_leaves_a_map() {
        // A short handset carrying the full five-row banner stack and a Mode A/B journey sheet.
        // CSS flex cannot go negative and simply collapses to zero; Compose measures what it is
        // told, so a remainder of -60 would be a map with no pixels rather than a small one.
        val crushed = homeMapNaturalHeight(viewport = 320.dp, banners = 140.dp, sheet = 240.dp)

        assertEquals(ControlTokens.HomeMapMinimum, crushed)
        assertTrue(crushed > 0.dp, "a map measured to zero or less is a map the driver cannot see")
    }
}
