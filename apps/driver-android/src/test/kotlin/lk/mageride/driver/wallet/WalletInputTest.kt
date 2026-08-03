package lk.mageride.driver.wallet

import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The two questions a keystroke has to answer before it becomes money.
 *
 * The Driver ID half is where the wireframe's `DRV-22011` meets the platform's actual identifier —
 * see [WalletInput]'s KDoc — and the amount half is the only place a typed string becomes minor
 * units.
 */
class WalletInputTest {

    @Test
    fun a_platform_id_is_a_driver_id_and_the_wireframes_drv_number_is_not() {
        assertTrue(WalletInput.isDriverId(Fixtures.DRIVER_ID), "a ULID is what every transfer route takes")
        assertTrue(
            WalletInput.isDriverId("01912d8a-7f3e-7c21-9f3b-1a2b3c4d5e6f"),
            "the same schema admits a canonical UUID — 36 characters with its hyphens",
        )

        // `_shared.yaml#/components/schemas/Ulid` is 26–36 characters, and the wireframe's mock is
        // nine. No route on the platform resolves one form into the other.
        assertFalse(WalletInput.isDriverId("DRV-22011"))
    }

    @Test
    fun a_malformed_id_is_refused_at_the_keyboard_rather_than_by_the_gateway() {
        assertFalse(WalletInput.isDriverId(""), "blank is 'not yet', and the CTA is simply not live")
        assertFalse(WalletInput.isDriverId("01JQ9F8Z6N000000000000000"), "25 characters — one short")
        assertFalse(WalletInput.isDriverId("01JQ9F8Z6N00000000000000000000000000000"), "39 — too long")
        // I, L, O and U are excluded from Crockford base32 precisely because they are misread.
        assertFalse(WalletInput.isDriverId("01JQ9F8Z6NILOU000000000001"))
    }

    @Test
    fun an_id_is_trimmed_and_never_rewritten() {
        // A paste out of a chat app brings whitespace; nothing else about an identifier is ours to
        // change. A ULID is upper-case and a UUID lower-case, so case-folding either breaks the
        // other — `Primitives.kt` is explicit that a client must pass a server id through.
        assertEquals(Fixtures.DRIVER_ID, WalletInput.driverId("  ${Fixtures.DRIVER_ID}\n"))

        val uuid = "01912d8a-7f3e-7c21-9f3b-1a2b3c4d5e6f"
        assertEquals(uuid, WalletInput.driverId(uuid), "the case is the server's, not ours")
    }

    @Test
    fun an_amount_is_whole_rupees_and_reaches_the_wire_as_minor_units() {
        assertEquals(200_000L, WalletInput.amountMinor("2000"), "Rs 2,000")
        assertEquals(200_000L, WalletInput.amountMinor("2,000"), "a pasted group separator survives")
        assertEquals(200_000L, WalletInput.amountMinor("Rs 2000"), "so does a pasted prefix")
    }

    @Test
    fun nothing_typed_and_a_typed_zero_are_the_same_disabled_button() {
        assertNull(WalletInput.amountMinor(""))
        assertNull(WalletInput.amountMinor("0"))
        assertNull(WalletInput.amountMinor("abc"))
    }

    @Test
    fun the_field_caps_the_digits_rather_than_letting_the_gateway_refuse_them() {
        // Nine digits is Rs 999,999,999 — far above any denomination and far below where an int64
        // `amountMinor` could overflow.
        assertEquals("999999999", WalletInput.rupeeDigits("99999999999999"))
        assertEquals(WalletInput.MAX_RUPEE_DIGITS, WalletInput.rupeeDigits("1234567890123").length)
        assertEquals("500", WalletInput.rupeeDigits("000500"), "a leading zero is not a digit of the amount")
    }

    @Test
    fun a_voucher_denomination_round_trips_through_the_field() {
        // Selecting a tile fills the amount box, and what it fills it with has to parse back to the
        // same minor units or the CTA would price something the driver did not pick.
        assertEquals("1000", WalletInput.rupeesOf(ONE_THOUSAND))
        assertEquals(ONE_THOUSAND, WalletInput.amountMinor(WalletInput.rupeesOf(ONE_THOUSAND)))
    }
}
