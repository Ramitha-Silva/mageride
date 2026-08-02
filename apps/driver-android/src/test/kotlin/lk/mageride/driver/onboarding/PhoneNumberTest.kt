package lk.mageride.driver.onboarding

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The `+947XXXXXXXX` shape D5' §14.1 fixes, and the four ways a driver writes it down.
 *
 * `POST /v1/auth/otp/request` takes E.164 and iam-svc rejects anything else, so every one of these
 * is a login that either works or does not — and the trunk-zero form is the one a driver reading
 * their number off the back of their own handset will type.
 */
class PhoneNumberTest {

    @Test
    fun the_four_ways_a_sri_lankan_number_is_written_all_normalise_to_the_same_nine_digits() {
        listOf(
            "771234567" to "the national significant number",
            "0771234567" to "with the trunk zero",
            "+94 77 123 4567" to "pasted in E.164, with spaces",
            "0094771234567" to "with the international access prefix",
        ).forEach { (input, form) ->
            assertEquals("771234567", PhoneNumber.normalise(input), form)
        }
    }

    @Test
    fun normalising_never_leaves_a_value_longer_than_the_field_accepts() {
        assertEquals(PhoneNumber.NATIONAL_LENGTH, PhoneNumber.normalise("7712345678901234").length)
    }

    @Test
    fun only_a_complete_mobile_number_is_valid() {
        assertTrue(PhoneNumber.isValid("771234567"))
        assertFalse(PhoneNumber.isValid("77123456"), "eight digits is incomplete")
        // Sri Lankan mobile numbers all begin with 7; a landline typed here would be accepted by
        // the field and refused by the SMS gateway, which is a worse place to find out.
        assertFalse(PhoneNumber.isValid("112345678"), "a Colombo landline is not a mobile")
    }

    @Test
    fun the_e164_form_is_the_country_code_and_the_national_number() {
        assertEquals("+94771234567", PhoneNumber.toE164("771234567"))
    }
}
