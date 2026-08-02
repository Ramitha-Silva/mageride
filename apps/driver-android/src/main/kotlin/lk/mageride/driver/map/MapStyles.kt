package lk.mageride.driver.map

import android.content.Context
import androidx.annotation.RawRes
import lk.mageride.driver.R

/**
 * The two MapLibre styles, read out of `res/raw` and pointed at the deployment's PMTiles archive.
 *
 * D2' §0.1 fixes the whole stack: *"Maps are MapLibre GL Native (Android/iOS SDK) over self-served
 * PMTiles on Cloudflare R2 — no Google Maps, no `JBridge`"*, and §0.2 gives the two appearances.
 * The style JSON carries the `__PMTILES_URL__` placeholder rather than a literal because the
 * archive moves between the dev compose stack and R2, and a style is a resource while the URL is a
 * build flag.
 *
 * `pmtiles://` is a scheme MapLibre Native resolves itself — the `.so` in the AAR carries the
 * PMTiles file source, so there is no protocol shim to register and no HTTP range-request code
 * here. That is the whole reason PMTiles was chosen over a tile server.
 *
 * **The cartography is deliberately thin** — background, earth, landuse, water, road casing and
 * fill, buildings, boundaries and place labels, against the Protomaps basemap's source-layer
 * names. It is a legible map, not a designed one; a full basemap style is a design asset rather
 * than something a shell should invent. See the C067 handoff.
 */
internal object MapStyles {

    private const val PLACEHOLDER = "__PMTILES_URL__"

    /** The light style with [pmTilesUrl] substituted in. */
    fun light(context: Context, pmTilesUrl: String): String = read(context, R.raw.map_style_light, pmTilesUrl)

    /** The dark style with [pmTilesUrl] substituted in. */
    fun dark(context: Context, pmTilesUrl: String): String = read(context, R.raw.map_style_dark, pmTilesUrl)

    /** The style for the appearance in force. */
    fun forTheme(context: Context, pmTilesUrl: String, darkTheme: Boolean): String =
        if (darkTheme) dark(context, pmTilesUrl) else light(context, pmTilesUrl)

    private fun read(context: Context, @RawRes resource: Int, pmTilesUrl: String): String =
        context.resources.openRawResource(resource)
            .bufferedReader()
            .use { it.readText() }
            .replace(PLACEHOLDER, pmTilesUrl)
}
