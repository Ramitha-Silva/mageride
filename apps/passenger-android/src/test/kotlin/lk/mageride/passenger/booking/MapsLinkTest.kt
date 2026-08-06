package lk.mageride.passenger.booking

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs

/**
 * AL-20, and the Definition-of-Done line that says *"all four Google Maps URL shapes resolve in the
 * paste sheet, and an unparseable link offers pick on map"*.
 *
 * This is the whole of the client half of the feature: D5' §BR-23.4 and D6' §I-23.1 both put full
 * URLs on the device and short links on transit-svc, and there is no Google SDK on either side of
 * that line. The URLs below are the shapes WhatsApp actually carries — a share sheet emits a short
 * link, a copied address bar emits `/maps/place/…`, and "share this pin" emits `?q=`.
 */
class MapsLinkTest {

    @Test
    fun a_place_url_resolves_to_the_pin_and_not_to_the_camera() {
        // The one that matters most and is easiest to get wrong. A /maps/place/… URL carries BOTH
        // an @lat,lng (where the camera was) and a !3d!4d (where the place is), and they are
        // routinely a hundred metres apart because the sender panned the map. Taking `@` would
        // drop the pickup pin on whatever was in the middle of their screen.
        val parsed = MapsLink.parse(
            "https://www.google.com/maps/place/Colombo+Fort/@6.9200000,79.8500000,15z/" +
                "data=!3m1!4b1!4m6!3m5!1s0x0:0x0!8m2!3d6.9344000!4d79.8428000",
        )

        val resolved = assertIs<MapsLinkParse.Resolved>(parsed)
        assertEquals(6.9344, resolved.point.lat, "the !3d place latitude, not the @ viewport one")
        assertEquals(79.8428, resolved.point.lng)
    }

    @Test
    fun a_shared_pin_resolves_from_its_query_parameter() {
        val parsed = MapsLink.parse("https://maps.google.com/?q=6.9344,79.8428")

        val resolved = assertIs<MapsLinkParse.Resolved>(parsed)
        assertEquals(6.9344, resolved.point.lat)
        assertEquals(79.8428, resolved.point.lng)
    }

    @Test
    fun the_encoded_and_prefixed_query_forms_are_the_same_answer() {
        // WhatsApp percent-encodes the comma, and the iOS share sheet emits `q=loc:`. Both are the
        // same URL to a passenger, so both have to be the same URL here.
        val encoded = assertIs<MapsLinkParse.Resolved>(MapsLink.parse("https://google.com/maps?q=6.9344%2C79.8428"))
        val prefixed = assertIs<MapsLinkParse.Resolved>(MapsLink.parse("https://google.com/maps?q=loc:6.9344,79.8428"))

        assertEquals(encoded.point, prefixed.point)
        assertEquals(6.9344, encoded.point.lat)
    }

    @Test
    fun a_bare_viewport_url_resolves_from_its_at_sign() {
        // No place, no query — the camera is all there is, so it IS the answer here.
        val parsed = MapsLink.parse("https://www.google.com/maps/@6.9344,79.8428,17z")

        val resolved = assertIs<MapsLinkParse.Resolved>(parsed)
        assertEquals(6.9344, resolved.point.lat)
    }

    @Test
    fun a_centre_parameter_beats_the_viewport_but_loses_to_the_query() {
        val centre = assertIs<MapsLinkParse.Resolved>(
            MapsLink.parse("https://maps.google.com/maps?ll=6.9344,79.8428&z=16"),
        )
        assertEquals(6.9344, centre.point.lat)

        // Both present: `q=` is what the sender asked for, `ll=` is where the map happened to sit.
        val both = assertIs<MapsLinkParse.Resolved>(
            MapsLink.parse("https://maps.google.com/maps?q=6.8000,79.9000&ll=6.9344,79.8428"),
        )
        assertEquals(6.8, both.point.lat, "the query wins")
    }

    @Test
    fun a_short_link_is_the_servers_to_resolve_and_not_a_failure() {
        // The one a share sheet actually produces. The coordinates are behind a redirect, and
        // following redirects is transit-svc's (D6' §I-23.1) — answering "unreadable" here would
        // send the passenger to "pick on map" when one HTTP call would have worked.
        val short = MapsLink.parse("https://maps.app.goo.gl/aBcDeFgH123")
        assertIs<MapsLinkParse.NeedsServer>(short)

        val legacy = MapsLink.parse("https://goo.gl/maps/aBcDeFgH123")
        assertIs<MapsLinkParse.NeedsServer>(legacy)
    }

    @Test
    fun a_google_url_with_a_place_name_rather_than_coordinates_goes_nowhere_local() {
        // `?q=Colombo+Fort` is a search term, not a point. It is not a short link either, so there
        // is nothing to resolve and the sheet offers the map.
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://www.google.com/maps?q=Colombo+Fort"))
    }

    @Test
    fun anything_that_is_not_a_google_maps_link_is_unreadable() {
        // Including a bare coordinate pair and a rival map's URL that happens to embed an @lat,lng
        // — accepting either would drop a pin from a string the platform never claimed to read.
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse(""))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("   "))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("6.9344,79.8428"))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://www.openstreetmap.org/#map=17/6.9344/79.8428"))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://example.com/maps/@6.9344,79.8428,17z"))
    }

    @Test
    fun a_coordinate_outside_the_world_is_not_a_coordinate() {
        // A truncated or mangled URL produces numbers that parse and cannot exist. `0,0` is the
        // one worth naming: Null Island is what a malformed link degrades to far more often than
        // it is what anybody meant.
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://www.google.com/maps?q=91.0,79.8428"))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://www.google.com/maps?q=6.9344,181.0"))
        assertIs<MapsLinkParse.Unreadable>(MapsLink.parse("https://www.google.com/maps?q=0.0,0.0"))
    }

    @Test
    fun a_southern_or_western_pin_keeps_its_sign() {
        // Sri Lanka is north and east, so a sign bug would never show up in a local test. It would
        // show up the first time somebody pasted a link from anywhere else.
        val parsed = assertIs<MapsLinkParse.Resolved>(
            MapsLink.parse("https://www.google.com/maps/@-33.8688,151.2093,15z"),
        )

        assertEquals(-33.8688, parsed.point.lat)
        assertEquals(151.2093, parsed.point.lng)
    }

    @Test
    fun a_country_domain_is_still_google() {
        // google.lk is what a Sri Lankan handset's browser produces, and it is the single most
        // likely host for a link pasted into this app.
        assertIs<MapsLinkParse.Resolved>(MapsLink.parse("https://www.google.lk/maps/@6.9344,79.8428,15z"))
        assertIs<MapsLinkParse.Resolved>(MapsLink.parse("https://www.google.co.uk/maps?q=6.9344,79.8428"))
    }
}
