package lk.mageride.driver.capture

import kotlin.test.Test
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * BR-28.4's *"auto edge-detect proposes a quad"*, exercised against pictures written by a `for`
 * loop.
 *
 * The point of taking a `LuminanceGrid` rather than a `Bitmap` is this file: a document on a
 * contrasting background is a shape a test can draw, and every branch of the proposal — found,
 * nothing there, and already filling the frame — is reachable on this build host.
 */
class DocumentEdgeDetectorTest {

    @Test
    fun a_dark_document_on_a_light_background_is_found() {
        val grid = frame(background = 230) { x, y -> if (x in 20..70 && y in 30..90) 40 else null }

        val quad = assertNotNull(DocumentEdgeDetector.propose(grid), "a 50×60 document in a 96×128 frame")

        // The proposal has to contain the document and not much else. Generous bounds on purpose:
        // this is a *proposal* the driver then drags (BR-28.4), not a measurement.
        assertTrue(quad.topLeft.x in 0.15f..0.25f, "left edge, got ${quad.topLeft.x}")
        assertTrue(quad.topRight.x in 0.70f..0.80f, "right edge, got ${quad.topRight.x}")
        assertTrue(quad.topLeft.y in 0.20f..0.28f, "top edge, got ${quad.topLeft.y}")
        assertTrue(quad.bottomLeft.y in 0.68f..0.76f, "bottom edge, got ${quad.bottomLeft.y}")
        assertTrue(quad.isUsable)
    }

    @Test
    fun a_light_document_on_a_dark_background_is_found_too() {
        // The detector works on distance from the background, not on "dark means document" — a
        // licence on a car seat and one on a dashboard are opposite polarities.
        val grid = frame(background = 30) { x, y -> if (x in 25..65 && y in 40..100) 220 else null }

        assertNotNull(DocumentEdgeDetector.propose(grid))
    }

    @Test
    fun an_even_frame_proposes_nothing_rather_than_guessing() {
        // A photograph of a wall. Proposing a box here would tell the driver a document had been
        // detected when there is none; the screen shows the default inset instead, which reads as
        // "put the document in this box".
        assertNull(DocumentEdgeDetector.propose(frame(background = 128) { _, _ -> null }))
    }

    @Test
    fun a_document_that_already_fills_the_frame_proposes_nothing() {
        // A document held right up to the lens: everything but the outermost ring differs from
        // it, so the bounding box IS the frame. That is not a finding and there is nothing to
        // crop, so the screen keeps the default proposal and the driver pulls the corners in.
        val grid = frame(background = 240) { x, y ->
            if (x in 1..94 && y in 1..126) 30 else null
        }

        assertNull(DocumentEdgeDetector.propose(grid))
    }

    @Test
    fun a_single_bright_speck_does_not_drag_an_edge_with_it() {
        // Row and column *profiles* rather than single pixels: a reflection off a laminated
        // licence is one bright cell, and a per-pixel bounding box would stretch to it.
        val grid = frame(background = 230) { x, y ->
            when {
                x in 20..70 && y in 30..90 -> 40
                x == 90 && y == 120 -> 20
                else -> null
            }
        }

        val quad = assertNotNull(DocumentEdgeDetector.propose(grid))

        assertTrue(
            quad.bottomRight.x < 0.85f,
            "the speck at x=90 did not pull the right edge, got ${quad.bottomRight.x}",
        )
    }

    @Test
    fun a_grid_too_coarse_to_say_anything_says_nothing() {
        assertNull(DocumentEdgeDetector.propose(LuminanceGrid(width = 8, height = 8, samples = IntArray(64))))
    }

    /** A 96 × 128 frame of [background], with [document] painting anything that is not it. */
    private fun frame(background: Int, document: (x: Int, y: Int) -> Int?): LuminanceGrid {
        val width = 96
        val height = 128
        val samples = IntArray(width * height)
        for (y in 0 until height) {
            for (x in 0 until width) {
                samples[y * width + x] = document(x, y) ?: background
            }
        }
        return LuminanceGrid(width, height, samples)
    }
}
