package lk.mageride.passenger.onboarding

import lk.mageride.shared.data.models.PhoneE164

/**
 * The one phone shape this platform accepts: `+947XXXXXXXX` (D5' §14.1).
 *
 * Sri Lanka is the only country MageRide operates in, so `+94` is a **prefix on the field** rather
 * than a country picker — every other dialling code is rejected downstream anyway. What is typed is
 * the national number, and the two ways a Sri Lankan number is written down (`0771234567` with the
 * trunk zero, `771234567` without) both have to work: a passenger reading their number off the back
 * of their own phone will type the first.
 */
internal object PhoneNumber {

    /** The dialling code shown as the field's prefix. */
    const val COUNTRY_CODE = "+94"

    /**
     * The wireframe's `7X XXX XXXX` hint.
     *
     * A digit mask, not copy: it is the same characters in Sinhala, Tamil and English, so putting
     * it in the three `strings.xml` files would mean three identical values —
     * `StringResourceTest` reads that (correctly) as a translation nobody did.
     */
    const val PLACEHOLDER = "7X XXX XXXX"

    /** National significant number length — nine digits, the first of which is always `7`. */
    const val NATIONAL_LENGTH = 9

    private const val MOBILE_LEADING_DIGIT = '7'

    /**
     * Strips everything a passenger might type around the digits, and the trunk `0`.
     *
     * Applied on every keystroke rather than on submit, so the field can never hold a value the
     * validator would reject — which is what makes a pasted `+94 77 123 4567` simply work.
     */
    fun normalise(input: String): String = input
        .filter(Char::isDigit)
        // Leading zeros first: that clears both the trunk `0` and the `00` international prefix,
        // after which `94` can only be the country code and never the start of a mobile number
        // (every one of those starts with a 7).
        .dropWhile { it == '0' }
        .removePrefix("94")
        .take(NATIONAL_LENGTH)

    /** Whether [national] is a complete Sri Lankan mobile number. */
    fun isValid(national: String): Boolean =
        national.length == NATIONAL_LENGTH && national.first() == MOBILE_LEADING_DIGIT

    /** The E.164 form `POST /v1/auth/otp/request` takes. Only call this on a [isValid] number. */
    fun toE164(national: String): PhoneE164 = "$COUNTRY_CODE$national"
}
