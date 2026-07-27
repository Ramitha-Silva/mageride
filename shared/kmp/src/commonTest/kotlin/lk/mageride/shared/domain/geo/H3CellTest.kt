package lk.mageride.shared.domain.geo

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The index layout, checked against real H3 output.
 *
 * The two tokens below are what `com.uber:h3` answers for Colombo Fort at resolutions 7 and 5
 * (`AndroidH3GridTest` re-derives them from the library rather than trusting these literals). They
 * are here so the pure-Kotlin half — the hex spelling, the resolution and base-cell bits — is
 * verified on **every** target, including the iOS ones where no engine is bound.
 */
class H3CellTest {

    @Test
    fun the_token_is_the_canonical_lowercase_hex_spelling() {
        val cell = H3Cell.parse(COLOMBO_FORT_RES7)

        assertEquals(COLOMBO_FORT_RES7, cell.token)
        assertEquals(COLOMBO_FORT_RES7, cell.toString(), "a cell interpolated into a group name")
        assertEquals(cell, H3Cell.parse(cell.token))
    }

    @Test
    fun the_resolution_is_read_out_of_the_index() {
        assertEquals(7, H3Cell.parse(COLOMBO_FORT_RES7).resolution)
        assertEquals(5, H3Cell.parse(COLOMBO_FORT_RES5).resolution)
    }

    @Test
    fun a_res_seven_cell_and_its_res_five_ancestor_share_a_base_cell() {
        assertEquals(
            H3Cell.parse(COLOMBO_FORT_RES5).baseCell,
            H3Cell.parse(COLOMBO_FORT_RES7).baseCell,
        )
    }

    @Test
    fun a_malformed_token_is_rejected_rather_than_silently_accepted() {
        // A group name built from a malformed cell subscribes to nothing, which looks exactly like
        // "no vehicles nearby" — so this fails loudly instead.
        assertNull(H3Cell.parseOrNull("not-hex"))
        assertNull(H3Cell.parseOrNull(""))
        assertNull(H3Cell.parseOrNull("0"), "mode 0 is not a cell index")
        assertNull(H3Cell.parseOrNull("ffffffffffffffff"), "the reserved high bit is set")
    }

    @Test
    fun a_digit_above_the_cells_own_resolution_must_be_the_unused_marker() {
        val valid = H3Cell.parse(COLOMBO_FORT_RES7)
        // Clear one of the trailing 7s: the index now claims resolution 7 but carries a real digit
        // in slot 8, which no H3 cell ever does.
        val tampered = H3Cell(valid.index and (0x7L shl 21).inv())

        assertTrue(valid.isWellFormed)
        assertFalse(tampered.isWellFormed)
    }

    @Test
    fun a_digit_inside_the_resolution_may_not_be_the_unused_marker() {
        val valid = H3Cell.parse(COLOMBO_FORT_RES7)
        val tampered = H3Cell(valid.index or (0x7L shl 42))

        assertFalse(tampered.isWellFormed, "digit 1 of a res-7 cell cannot be 7")
    }

    private companion object {
        /** `latLngToCell(6.9271, 79.8612, 7)`. */
        const val COLOMBO_FORT_RES7 = "87611cb11ffffff"

        /** `latLngToCell(6.9271, 79.8612, 5)`. */
        const val COLOMBO_FORT_RES5 = "85611cb3fffffff"
    }
}
