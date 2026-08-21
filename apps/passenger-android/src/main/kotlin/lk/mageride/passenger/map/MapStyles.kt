package lk.mageride.passenger.map

import android.content.Context
import androidx.annotation.RawRes
import lk.mageride.passenger.R

/**
 * MAP-01's two styles, read out of `res/raw` and pointed at the deployment's PMTiles archive.
 *
 * D2' §0.1 fixes the whole stack: *"Maps are MapLibre GL Native (Android/iOS SDK) over self-served
 * PMTiles on Cloudflare R2 — no Google Maps, no `JBridge`"*, and MAP-01 asks for *"a custom style
 * (dark mode + light mode)"*. The style JSON carries the `__PMTILES_URL__` placeholder rather than
 * a literal because the archive moves between the dev compose stack and R2, and a style is a
 * resource while the URL is a build flag.
 *
 * `pmtiles://` is a scheme MapLibre Native resolves itself — the `.so` in the AAR carries the
 * PMTiles file source, so there is no protocol shim to register and no HTTP range-request code
 * here. That is the whole reason PMTiles was chosen over a tile server.
 *
 * **The cartography is deliberately thin** — background, earth, landuse, water, road casing and
 * fill, buildings, boundaries and place labels, against the Protomaps basemap's source-layer
 * names. It is a legible map, not a designed one; a full basemap style is a design asset rather
 * than something a shell should invent. The two JSON files are byte-identical to the driver app's,
 * which is what makes a junction look the same to both sides of a ride.
 *
 *
 * ### The labels on this map are not trilingual, and cannot be
 *
 * D-26 makes every user-facing string Si/Ta/En and the basemap's own place names are the one
 * exception on the platform. `["get", "name"]` in both style files is a **ceiling, not an
 * oversight**, and it is three walls deep — each of which alone is enough:
 *
 * 1. **The tiles carry no Sinhala or Tamil.** `infra/replica/tiles/deploy-tiles.sh` cuts the
 *    archive from the Protomaps basemap, which carries `name:xx` for 41 languages. `si` and `ta`
 *    are not among them, so there is no field for a style to ask for.
 * 2. **MapLibre cannot render either script.** Both are complex scripts needing reordering and
 *    ligature shaping; MapLibre Native draws SDF glyphs per codepoint with no shaping engine
 *    (maplibre-native#706 is still open). Protomaps' own localized styles list Sinhalese and Tamil
 *    under "no MapLibre support" and **hide** text in those scripts rather than draw it wrong.
 * 3. **The glyph server has no such glyphs.** `glyphs` points at `fonts/{fontstack}`, the stack is
 *    `Inter Regular`, and the live box symlinks that to **Noto Sans Regular** — which covers
 *    neither block. Those SDF ranges were never generated.
 *
 * Getting there means building our own tiles with `name:si` from the Sri Lanka OSM extract *and*
 * shipping a positioned-glyph font (HarfBuzz-shaped, per Protomaps' Devanagari precedent). That is
 * a project, not a style edit. **Do not add a language parameter to this object expecting it to
 * work** — the honest failure is a label that renders as broken glyph sequences.
 *
 * Everything the app draws ITSELF is unaffected: markers, pin labels and sheet copy are Compose
 * text and render Sinhala correctly. The geocoder is trilingual too — `GET /v1/geo/search` and
 * `/v1/geo/reverse` take a `lang` — so a place a driver or passenger searches for reads in their
 * language even where the map underneath it does not.
 *
 * **MAP-09 (offline tile caching) is not here** — see the C076 handoff. It needs a signed-bundle
 * download that D3' explicitly says is *"not an app-facing API"*, and `mobile_db_schema.md` §1.9's
 * `offline_map_bundles` has no writer anywhere on the platform yet.
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
