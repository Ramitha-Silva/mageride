package lk.mageride.driver.wallet

import lk.mageride.driver.ui.MoneyFormat
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The two things SCR-DA-022 draws that are neither state nor copy: a discount, and a code.
 *
 * Both look like presentation and are not. A tier printed as `10%` when it is `12.5%` misprices a
 * voucher on the tile the driver taps, and a payload that silently fails to encode leaves AL-15's
 * fallback as an empty white square on a screen whose whole purpose is to be scanned.
 */
class WalletRenderingTest {

    @Test
    fun a_whole_percent_tier_prints_without_a_decimal_and_a_fractional_one_keeps_it() {
        // The ladder is stored in basis points because a percentage of money has to survive
        // `FareRounding` as an exact rational (C016); a driver reads a percentage.
        assertEquals("10%", MoneyFormat.percentOfBps(1_000))
        assertEquals("18%", MoneyFormat.percentOfBps(1_800))
        assertEquals("12.5%", MoneyFormat.percentOfBps(1_250))
        assertEquals("12.05%", MoneyFormat.percentOfBps(1_205))
        assertEquals("0%", MoneyFormat.percentOfBps(0))
        assertEquals("100%", MoneyFormat.percentOfBps(10_000))
    }

    @Test
    fun an_unknown_amount_is_a_dash_and_never_a_zero_balance() {
        // Zero is a balance a driver can have; being told they have it when nothing was read is
        // worse than being told nothing.
        assertEquals("—", MoneyFormat.EMPTY)
        assertEquals("Rs 0", MoneyFormat.rupees(0L))
    }

    @Test
    fun an_emvco_payload_encodes_to_a_square_module_grid() {
        // A real LankaQR-shaped payload: EMVCo TLV, ASCII throughout, a couple of hundred bytes.
        val payload = buildString {
            append("00020101021230560016A00000067701011201")
            append("0115901234567890123")
            append("5204541153030445802LK5909MageRide6007Colombo")
            append("62070503***6304")
        }

        val matrix = assertNotNull(lankaQrMatrix(payload), "an EMVCo payload must encode")
        assertEquals(matrix.width, matrix.height, "a QR symbol is square")

        // The bare module grid, not a scaled bitmap: a QR version is 21 + 4n modules a side, so a
        // matrix in the hundreds would mean ZXing had rendered pixels and the canvas would be
        // drawing a quarter of a million rectangles a frame. See MODULE_GRID.
        assertTrue(matrix.width in MIN_MODULES..MAX_MODULES, "expected a module grid, got ${matrix.width}")
        assertEquals(0, (matrix.width - MIN_MODULES) % MODULE_STEP, "a QR version is 21 + 4n modules a side")

        // Margin 0 — the composable draws the quiet zone as padding on a white field, and ZXing's
        // default four-module margin would be drawn *inside* the same square and halve the code.
        assertTrue(matrix.get(0, 0), "the top-left finder pattern starts at the very first module")
    }

    @Test
    fun a_payload_that_cannot_be_encoded_leaves_the_screen_standing() {
        // The payload comes from a gateway. A malformed one must not take the app down on a screen
        // that still has a working OnePay tile on it.
        assertNull(lankaQrMatrix(""))
    }

    private companion object {
        /** QR version 1 — the smallest symbol there is. */
        const val MIN_MODULES = 21

        /** QR version 40 — the largest. */
        const val MAX_MODULES = 177

        /** Each version adds four modules a side. */
        const val MODULE_STEP = 4
    }
}
