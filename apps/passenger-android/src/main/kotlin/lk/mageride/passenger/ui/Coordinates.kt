package lk.mageride.passenger.ui

import lk.mageride.shared.data.models.GeoPoint
import java.util.Locale

/**
 * `6.93440, 79.84280` — a coordinate pair, for the screens that show one.
 *
 * **A Kotlin constant rather than a `strings.xml` entry**, for the reason `LanguageNames`,
 * `PhoneNumber` and `MoneyFormat.PREFIX` are: it is digits and a comma, identical in all three
 * scripts, and three identical values in the three files is precisely what `StringResourceTest`
 * reads as a translation nobody did. A sentence *containing* a coordinate is still copy — see
 * `capture_pinned` — and stays in `strings.xml` where it belongs.
 *
 * Five decimals, which is about a metre and is the resolution the encoded-polyline and geocoding
 * formats both stop at. `Locale.ROOT` because a coordinate is not a locale-formatted number: a
 * decimal comma would turn `6,93440, 79,84280` into four fields.
 */
internal object Coordinates {

    fun format(lat: Double, lng: Double): String =
        String.format(Locale.ROOT, "%.${DECIMALS}f, %.${DECIMALS}f", lat, lng)

    fun format(point: GeoPoint): String = format(point.lat, point.lng)

    /** About a metre. Finer than any geocoder answers and coarser than float noise. */
    private const val DECIMALS = 5
}
