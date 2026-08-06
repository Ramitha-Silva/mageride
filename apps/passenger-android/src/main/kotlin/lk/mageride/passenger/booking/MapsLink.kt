package lk.mageride.passenger.booking

import lk.mageride.shared.data.models.GeoPoint

/**
 * What a pasted string turned out to be.
 *
 * Three outcomes rather than a nullable point, because the middle one is a *different action*: a
 * short link is a perfectly good link that this device cannot read, and answering `null` for it
 * would send the passenger to "pick on map" when one HTTP call would have worked.
 */
internal sealed interface MapsLinkParse {

    /** Coordinates were in the URL. No network needed. */
    data class Resolved(val point: GeoPoint) : MapsLinkParse

    /**
     * A shortened link — `maps.app.goo.gl` or `goo.gl/maps`.
     *
     * The coordinates are behind a redirect, and following redirects is transit-svc's job
     * (`GET /v1/geo/parse-maps-link`): a short link is resolved server-side *"via a single
     * server-side HTTP redirect follow … no Google API"* (D6' §I-23.1).
     */
    data class NeedsServer(val url: String) : MapsLinkParse

    /** Not a link this platform understands. SCR-PA-012a's Error state — "pick on map". */
    data object Unreadable : MapsLinkParse
}

/**
 * AL-20's Google-Maps paste, parsed on the device.
 *
 * D5' §BR-23.4 and D6' §I-23.1 both say the same thing and it is the whole design: **full URLs are
 * parsed client-side, short links go to transit-svc.** No Google SDK, no API key, no billing
 * relationship — a Google Maps URL is a string, and the coordinates in it are already there.
 *
 * The five forms the specs enumerate, in the order this object tries them:
 *
 * | Form | Example | Why here in the order |
 * |---|---|---|
 * | `!3d…!4d…` | `…/data=!3m1!4b1!4d79.86!3d6.93` | the **place's own** coordinate |
 * | `q=` / `query=` | `?q=6.93,79.86` | an explicit request for a point |
 * | `ll=` / `center=` | `?ll=6.93,79.86` | an explicit map centre |
 * | `@lat,lng,zoom` | `/maps/@6.93,79.86,15z` | the **viewport**, which is not the pin |
 *
 * **`!3d!4d` beats `@`, and that is not arbitrary.** A `/maps/place/…` URL usually carries both:
 * `@` is where the camera was and `!3d!4d` is where the *place* is, and they are routinely a
 * hundred metres apart because the map was panned. Preferring the viewport would drop a pickup pin
 * on whatever was in the middle of the sender's screen. `q=` outranks `@` for the same reason.
 *
 * **A `q=` that is not a coordinate is not a failure of this parser** — `?q=Colombo+Fort` is a
 * search term, and the caller falls through to the next form and then to the server.
 */
internal object MapsLink {

    /** Reads [input] as far as this device can. */
    @Suppress("ReturnCount") // One per outcome: not a Maps link, coordinates found, short, unreadable.
    fun parse(input: String): MapsLinkParse {
        val url = input.trim()
        if (url.isEmpty()) return MapsLinkParse.Unreadable
        if (GOOGLE_HOSTS.none { url.contains(it, ignoreCase = true) }) return MapsLinkParse.Unreadable

        coordinates(url)?.let { return MapsLinkParse.Resolved(it) }
        if (SHORT_HOSTS.any { url.contains(it, ignoreCase = true) }) return MapsLinkParse.NeedsServer(url)
        return MapsLinkParse.Unreadable
    }

    /**
     * The first coordinate pair any of the four forms yields, or `null`.
     *
     * Separate from [parse] so the ordering above is one readable expression rather than four
     * nested `if`s, and so a test can ask "what did this URL contain" without a verdict attached.
     */
    private fun coordinates(url: String): GeoPoint? =
        placePin(url) ?: parameterPair(url, QUERY_KEYS) ?: parameterPair(url, CENTRE_KEYS) ?: viewport(url)

    /**
     * `!3d<lat>!4d<lng>` — the place pin inside a `/data=` blob.
     *
     * The two are **not adjacent** in a real URL (`!3m1!1e3!4m…!3d6.93!4d79.86`) and `!4d` can even
     * precede `!3d`, so each is matched on its own rather than as one pattern.
     */
    private fun placePin(url: String): GeoPoint? = point(
        lat = PLACE_LAT.find(url)?.groupValues?.get(1)?.toDoubleOrNull(),
        lng = PLACE_LNG.find(url)?.groupValues?.get(1)?.toDoubleOrNull(),
    )

    /** `?q=lat,lng`, `?query=lat,lng`, `?ll=lat,lng`, `?center=lat,lng` — and `q=loc:lat,lng`. */
    private fun parameterPair(url: String, keys: List<String>): GeoPoint? = keys.firstNotNullOfOrNull { key ->
        Regex("""[?&]$key=(?:loc:)?(-?\d+\.?\d*)(?:,|%2C)(-?\d+\.?\d*)""", RegexOption.IGNORE_CASE)
            .find(url)
            ?.let { point(it.groupValues[1].toDoubleOrNull(), it.groupValues[2].toDoubleOrNull()) }
    }

    /** `/@lat,lng,15z` — the camera, and the last thing tried for the reason in the class KDoc. */
    private fun viewport(url: String): GeoPoint? = VIEWPORT.find(url)?.let {
        point(it.groupValues[1].toDoubleOrNull(), it.groupValues[2].toDoubleOrNull())
    }

    /**
     * A pair, if it is a coordinate at all.
     *
     * Range-checked and nothing more. A link to somewhere outside the operating cities is a
     * *serviceability* answer — `400 unserviceable-area` on the fare estimate, in trilingual copy
     * the platform owns — not a parse failure, and rejecting it here would tell a passenger their
     * link was unreadable when it was read perfectly.
     */
    private fun point(lat: Double?, lng: Double?): GeoPoint? {
        if (lat == null || lng == null) return null
        val inRange = lat in -MAX_LAT..MAX_LAT && lng in -MAX_LNG..MAX_LNG
        // `0,0` is in the Gulf of Guinea and is what a malformed URL degrades to far more often
        // than it is what somebody meant. Null Island is not a pickup point.
        val nullIsland = lat == 0.0 && lng == 0.0
        return if (inRange && !nullIsland) GeoPoint(lat = lat, lng = lng) else null
    }

    /**
     * Every form this object reads lives on one of these hosts.
     *
     * Checked before the coordinate regexes rather than after, so a bare `6.93,79.86` — or a link
     * to some other mapping site that happens to embed an `@lat,lng` — is *"couldn't read that
     * link"* rather than a silently accepted pin. Country domains (`google.lk`, `google.co.uk`)
     * are covered by the `google.` prefix.
     */
    private val GOOGLE_HOSTS = listOf("google.", "goo.gl", "maps.app.goo.gl")

    private val SHORT_HOSTS = listOf("maps.app.goo.gl", "goo.gl/maps")

    private val QUERY_KEYS = listOf("q", "query", "daddr", "destination")
    private val CENTRE_KEYS = listOf("ll", "center", "sll")

    private val PLACE_LAT = Regex("""!3d(-?\d+\.?\d*)""")
    private val PLACE_LNG = Regex("""!4d(-?\d+\.?\d*)""")
    private val VIEWPORT = Regex("""@(-?\d+\.\d+),(-?\d+\.\d+)""")

    private const val MAX_LAT = 90.0
    private const val MAX_LNG = 180.0
}
