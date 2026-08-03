package lk.mageride.driver.tracker

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-027's two questions, answered off a device.
 *
 * The pattern is `provisioning.yaml#/components/schemas/Imei` — `^\d{15}$` — and the QR reader is
 * deliberately a *search* rather than a parse, because no spec says what a tracker vendor prints in
 * its code.
 */
class TrackerImeiTest {

    @Test
    fun a_field_keeps_fifteen_digits_and_drops_everything_else() {
        // An IMEI is read off a sticker, so it arrives grouped, hyphenated, or with a stray space.
        assertEquals(TRACKER_IMEI, TrackerImei.digits("8612 3456 7890 123"))
        assertEquals(TRACKER_IMEI, TrackerImei.digits("861234-56-789012-3"))
        assertEquals(TRACKER_IMEI, TrackerImei.digits(" $TRACKER_IMEI\n"))
        assertEquals(TRACKER_IMEI, TrackerImei.digits("${TRACKER_IMEI}9999"), "the field stops at fifteen")
    }

    @Test
    fun only_fifteen_digits_is_an_imei() {
        assertTrue(TrackerImei.isValid(TRACKER_IMEI))
        assertTrue(TrackerImei.isValid("8612 3456 7890 123"))
        // Separators and a pasted label are stripped before the check, which is the same reduction
        // the field itself applies on every keystroke — so what is validated is always what is
        // shown, and a paste of `IMEI:861234567890123` simply works.
        assertTrue(TrackerImei.isValid("IMEI:$TRACKER_IMEI"))

        assertFalse(TrackerImei.isValid(""), "blank is 'not yet', and the CTA is dead either way")
        assertFalse(TrackerImei.isValid("86123456789012"), "fourteen")
        assertFalse(TrackerImei.isValid("IMEI 8612-3456-7890-12"), "still fourteen once reduced")
    }

    @Test
    fun there_is_no_luhn_check_and_that_is_deliberate() {
        // The last digit of a real IMEI is a Luhn check digit and NEITHER contract asks for one.
        // `861234567890123` fails Luhn; refusing it here would make a tracker whose serial the
        // server would have accepted unpairable at the roadside, with no way round it.
        assertTrue(TrackerImei.isValid("861234567890123"))
        assertTrue(TrackerImei.isValid("000000000000000"))
    }

    @Test
    fun a_device_qr_yields_its_imei_whatever_shape_it_is_printed_in() {
        assertEquals(TRACKER_IMEI, TrackerImei.imeiIn(TRACKER_IMEI))
        assertEquals(TRACKER_IMEI, TrackerImei.imeiIn("IMEI:$TRACKER_IMEI"))
        assertEquals(TRACKER_IMEI, TrackerImei.imeiIn("https://prov.example/bind?imei=$TRACKER_IMEI&v=2"))
        // The same number twice is still one candidate — a label that prints it in a URL and again
        // as text is the common case, not an ambiguity.
        assertEquals(TRACKER_IMEI, TrackerImei.imeiIn("$TRACKER_IMEI\nIMEI:$TRACKER_IMEI"))
    }

    @Test
    fun a_longer_run_of_digits_is_not_an_imei_hiding_inside_it() {
        // A 20-digit ICCID is printed on the same sticker as the IMEI on most trackers. Taking its
        // first fifteen characters would bind a serial that mints a credential and never connects.
        assertNull(TrackerImei.imeiIn("ICCID:89940011223344556677"))
        assertNull(TrackerImei.imeiIn("${TRACKER_IMEI}0"))
    }

    @Test
    fun two_different_candidates_are_refused_rather_than_guessed_between() {
        assertNull(
            TrackerImei.imeiIn("$TRACKER_IMEI $OTHER_IMEI"),
            "picking the first would bind whichever the vendor happened to print first",
        )
    }

    @Test
    fun a_paired_imei_reads_back_the_way_the_wireframe_prints_it() {
        assertEquals("8612 3456 7890 123", TrackerImei.grouped(TRACKER_IMEI))
    }
}
