package lk.mageride.passenger.onboarding

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * `+947XXXXXXXX` (D5' §14.1), and the several ways a Sri Lankan writes their own number.
 *
 * The normalisation runs on **every keystroke**, so the field can never hold a value the validator
 * would reject. That is what makes a pasted `+94 77 123 4567` simply work — and it is the only
 * reason `INVALID_PHONE` is close to unreachable from this screen.
 */
class PhoneNumberTest {

    @Test
    fun the_three_ways_a_number_is_written_all_normalise_to_the_same_nine_digits() {
        val expected = "771234567"

        // As typed off the back of a handset, with the trunk zero.
        assertEquals(expected, PhoneNumber.normalise("0771234567"))
        // As spoken — no trunk zero.
        assertEquals(expected, PhoneNumber.normalise("771234567"))
        // As pasted from a contact card, spaces and dialling code and all.
        assertEquals(expected, PhoneNumber.normalise("+94 77 123 4567"))
        // As pasted from a website that uses the `00` international prefix.
        assertEquals(expected, PhoneNumber.normalise("0094771234567"))
    }

    @Test
    fun everything_that_is_not_a_digit_is_dropped() {
        assertEquals("771234567", PhoneNumber.normalise("(077) 123-4567"))
        assertEquals("", PhoneNumber.normalise("not a number"))
    }

    @Test
    fun the_field_cannot_hold_more_than_a_national_number() {
        // Nine digits is the whole of it, so a paste with a trailing extension is truncated rather
        // than making the field invalid in a way the passenger cannot see.
        assertEquals(PhoneNumber.NATIONAL_LENGTH, PhoneNumber.normalise("0771234567890").length)
    }

    @Test
    fun only_a_complete_mobile_number_is_valid() {
        assertTrue(PhoneNumber.isValid("771234567"))
        assertFalse(PhoneNumber.isValid("77123456"), "eight digits")
        assertFalse(PhoneNumber.isValid(""), "empty")
        // Sri Lankan mobiles all begin with 7; 011 is a Colombo landline, and dispatch has no way
        // to send an SMS to one.
        assertFalse(PhoneNumber.isValid("112345678"), "a landline")
    }

    @Test
    fun the_wire_form_is_what_the_otp_request_takes() {
        assertEquals("+94771234567", PhoneNumber.toE164("771234567"))
        assertEquals("+94", PhoneNumber.COUNTRY_CODE)
    }
}
