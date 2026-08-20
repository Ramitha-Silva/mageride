package lk.mageride.driver.capture

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The crop quadrilateral's rules — AL-43's *"the driver drags the four corner handles"*, minus the
 * camera.
 *
 * Every one of these is a failure that would otherwise be found by dragging a handle on a handset
 * and looking at a picture that came out folded, mirrored or half black. The geometry is pure
 * Kotlin precisely so that it can be found here instead.
 */
class CropQuadTest {

    @Test
    fun the_corners_wind_top_left_first_which_is_the_order_the_perspective_transform_needs() {
        // `Matrix.setPolyToPoly` maps source points onto destination points PAIRWISE. The
        // destination is built in this order in `DocumentImaging`, so a quad that answered its
        // corners in another order would produce a rotated or mirrored document, not a de-skewed
        // one — and it would look like a plausible photograph, which is what makes it dangerous.
        val quad = CropQuad.rectangle(left = 0.1f, top = 0.2f, right = 0.9f, bottom = 0.8f)

        assertEquals(
            listOf(
                QuadPoint(0.1f, 0.2f),
                QuadPoint(0.9f, 0.2f),
                QuadPoint(0.9f, 0.8f),
                QuadPoint(0.1f, 0.8f),
            ),
            quad.corners,
        )
    }

    @Test
    fun a_drag_outside_the_frame_stops_at_the_edge() {
        val moved = CropQuad.DEFAULT.moved(CropCorner.TOP_LEFT, QuadPoint(-0.4f, -1.2f))

        assertEquals(QuadPoint(0f, 0f), moved.topLeft, "a thumb can leave the frame; a corner cannot")
    }

    @Test
    fun a_corner_dragged_past_its_neighbour_is_refused_rather_than_folding_the_quad() {
        // A crossed quad is not an error `setPolyToPoly` reports — it produces a folded image. The
        // handle has to stop at the fold, which is what "refuse the move" means here.
        val quad = CropQuad.DEFAULT
        val folded = quad.moved(CropCorner.TOP_LEFT, QuadPoint(0.95f, 0.5f))

        assertEquals(quad, folded, "the move crosses the top edge over the right edge")
    }

    @Test
    fun a_quad_squeezed_below_a_document_shaped_side_is_refused_too() {
        val quad = CropQuad.rectangle(left = 0.4f, top = 0.4f, right = 0.6f, bottom = 0.9f)

        // Convex, but only 0.02 wide at the top — a sliver, not a document. Convexity alone does
        // not catch this, which is why `isUsable` asks both questions.
        val sliver = quad.moved(CropCorner.TOP_RIGHT, QuadPoint(0.42f, 0.4f))

        assertEquals(quad, sliver)
        assertTrue(quad.isUsable)
    }

    @Test
    fun a_legitimate_drag_is_applied() {
        val moved = CropQuad.DEFAULT.moved(CropCorner.BOTTOM_RIGHT, QuadPoint(0.8f, 0.7f))

        assertEquals(QuadPoint(0.8f, 0.7f), moved.bottomRight)
        assertTrue(moved.isUsable)
    }

    @Test
    fun a_dragged_corner_carries_its_neighbours_so_the_box_stays_a_rectangle() {
        // The defect this replaced: dragging one corner moved only that corner, so the box became
        // a trapezium under the thumb and the driver was cropping a shape nothing else on this
        // screen produces — `DocumentEdgeDetector` only ever proposes an axis-aligned box.
        val moved = CropQuad.DEFAULT.moved(CropCorner.TOP_LEFT, QuadPoint(0.3f, 0.4f))

        assertEquals(QuadPoint(0.3f, 0.4f), moved.topLeft)
        assertEquals(0.4f, moved.topRight.y, "the top edge moved with the corner")
        assertEquals(0.3f, moved.bottomLeft.x, "and so did the left edge")
        assertEquals(CropQuad.DEFAULT.bottomRight, moved.bottomRight, "the opposite corner is the anchor")
        assertTrue(moved.isRectangle())
    }

    @Test
    fun every_corner_drags_to_a_rectangle_not_only_the_first_one() {
        // One `when` arm with the wrong pair of edges in it would distort only on that handle,
        // which is exactly the kind of thing that reaches a handset.
        CropCorner.entries.forEach { corner ->
            val moved = CropQuad.DEFAULT.moved(corner, QuadPoint(0.45f, 0.55f))

            assertTrue(moved.isRectangle(), "$corner left the box skewed")
            assertEquals(QuadPoint(0.45f, 0.55f), moved.corner(corner), "$corner did not reach the touch")
        }
    }

    @Test
    fun a_corner_dragged_past_the_opposite_edge_is_refused_rather_than_mirroring_the_box() {
        // A mirrored rectangle is still four long sides wound the same way — convexity does not
        // catch it, and `setPolyToPoly` would hand back a flipped document rather than refuse.
        val quad = CropQuad.rectangle(left = 0.2f, top = 0.2f, right = 0.8f, bottom = 0.8f)

        assertEquals(quad, quad.moved(CropCorner.TOP_LEFT, QuadPoint(0.9f, 0.5f)), "dragged past the right edge")
        assertEquals(quad, quad.moved(CropCorner.BOTTOM_RIGHT, QuadPoint(0.5f, 0.1f)), "dragged above the top edge")
    }

    @Test
    fun the_output_takes_the_longer_of_each_pair_of_opposite_sides() {
        // A document photographed at an angle has a near edge longer than its far edge. Taking the
        // shorter one would resample the near half downwards and throw away detail that was
        // actually captured — on a number plate, that is the difference between a 7 and a 1.
        val skewed = CropQuad(
            topLeft = QuadPoint(0.2f, 0f),
            topRight = QuadPoint(0.8f, 0f),
            bottomRight = QuadPoint(1f, 1f),
            bottomLeft = QuadPoint(0f, 1f),
        )

        val (width, height) = skewed.outputSize(sourceWidth = 1000, sourceHeight = 1000)

        assertEquals(1000, width, "the bottom edge is the full frame; the top is 600 px")
        assertTrue(height in 1000..1100, "the sides are 1000 px tall plus their lean, got $height")
    }

    @Test
    fun a_huge_source_is_capped_so_a_budget_handset_can_allocate_the_result() {
        // URD NFR-22's floor is Android 8.0, which is a 1 GB handset. A full-frame warp of a 12 MP
        // capture at ARGB_8888 is ~48 MB and does not survive the allocation.
        val (width, height) = CropQuad.FULL.outputSize(sourceWidth = 4032, sourceHeight = 3024)

        assertEquals(2400, width)
        assertEquals(1800, height, "the aspect ratio survives the cap")
    }

    @Test
    fun the_default_proposal_is_inset_so_every_handle_can_be_reached() {
        // A quad on the frame edge has no handle a thumb can get behind, and the driver's first
        // drag would be a pan of the corner they could not grab.
        assertFalse(CropQuad.DEFAULT.isWholeFrame)
        assertTrue(CropQuad.FULL.isWholeFrame)
        assertTrue(CropQuad.DEFAULT.isUsable)
        assertTrue(CropQuad.DEFAULT.corners.all { it.x > 0f && it.y > 0f && it.x < 1f && it.y < 1f })
    }

    /** Whether the four corners still make an axis-aligned box. */
    private fun CropQuad.isRectangle(): Boolean = topLeft.y == topRight.y &&
        bottomLeft.y == bottomRight.y &&
        topLeft.x == bottomLeft.x &&
        topRight.x == bottomRight.x
}
