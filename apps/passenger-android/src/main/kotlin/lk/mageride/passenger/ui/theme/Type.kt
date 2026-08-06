package lk.mageride.passenger.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import lk.mageride.passenger.R

// D2' §0.2 — "Android: Material 3 type scale, font Outfit (display/headline) + Inter (body)".
//
// Both families ship in `res/font` as their upstream VARIABLE fonts (`wght` axis), which is why
// one file covers every weight: Compose's `Font(resId, weight)` fills in
// `FontVariation.Settings(weight, style)` by default, and Android has honoured `wght` since API
// 26 — exactly the URD NFR-22 floor. Static per-weight cuts do not exist upstream for either
// family. Licences: `res/raw/ofl_outfit.txt`, `res/raw/ofl_inter.txt` (SIL OFL 1.1, both
// redistributable in a bundled binary).
//
// Sinhala and Tamil are NOT covered by Outfit or Inter, and this is deliberate rather than an
// oversight: neither family has those scripts, so the platform falls back to Noto Sans Sinhala /
// Noto Sans Tamil, which every Android 8.0 handset ships. A si/ta string therefore renders in a
// face the spec never chose — the sizes and weights below still apply. Same finding as C067's.

private val Outfit = FontFamily(
    Font(R.font.outfit_variable, FontWeight.SemiBold),
    Font(R.font.outfit_variable, FontWeight.Bold),
)

private val Inter = FontFamily(
    Font(R.font.inter_variable, FontWeight.Normal),
    Font(R.font.inter_variable, FontWeight.Medium),
    Font(R.font.inter_variable, FontWeight.SemiBold),
    Font(R.font.inter_variable, FontWeight.Bold),
)

/**
 * The eight rows of D2' §0.2's type table, mapped onto the M3 tokens the table itself names.
 *
 * Only those eight are overridden. The remaining M3 roles keep their defaults *with the Inter
 * family applied* — a screen that reaches for `displayLarge` gets a sane style rather than
 * Roboto in the middle of a MageRide layout.
 *
 * Line heights are not in the spec's table; each is the M3 default ratio for its role, rounded to
 * the 4 dp grid §0.2 fixes for spacing.
 */
internal val MageRideTypography: Typography = run {
    val base = Typography()
    base.copy(
        // Display · displaySmall · 32 / 700 · Outfit
        displayLarge = base.displayLarge.copy(fontFamily = Outfit),
        displayMedium = base.displayMedium.copy(fontFamily = Outfit),
        displaySmall = TextStyle(
            fontFamily = Outfit,
            fontWeight = FontWeight.Bold,
            fontSize = 32.sp,
            lineHeight = 40.sp,
        ),
        // Headline · headlineMedium · 22 / 700 · Outfit
        headlineLarge = base.headlineLarge.copy(fontFamily = Outfit),
        headlineMedium = TextStyle(
            fontFamily = Outfit,
            fontWeight = FontWeight.Bold,
            fontSize = 22.sp,
            lineHeight = 28.sp,
        ),
        headlineSmall = base.headlineSmall.copy(fontFamily = Outfit),
        // Title · titleLarge · 18 / 600 · Inter
        titleLarge = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.SemiBold,
            fontSize = 18.sp,
            lineHeight = 24.sp,
        ),
        // Subtitle · titleMedium · 16 / 600 · Inter
        titleMedium = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.SemiBold,
            fontSize = 16.sp,
            lineHeight = 24.sp,
        ),
        titleSmall = base.titleSmall.copy(fontFamily = Inter),
        // Body · bodyLarge · 16 / 400 · Inter. The table's "400/500" is one role with an
        // emphasis variant, not two tokens — a screen that needs the heavier cut copies this
        // style with FontWeight.Medium rather than reaching for a different role.
        bodyLarge = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.Normal,
            fontSize = 16.sp,
            lineHeight = 24.sp,
        ),
        // Body small · bodyMedium · 14 / 400 · Inter
        bodyMedium = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.Normal,
            fontSize = 14.sp,
            lineHeight = 20.sp,
        ),
        bodySmall = base.bodySmall.copy(fontFamily = Inter),
        labelLarge = base.labelLarge.copy(fontFamily = Inter),
        // Label / tag · labelMedium · 12 / 500 · Inter
        labelMedium = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.Medium,
            fontSize = 12.sp,
            lineHeight = 16.sp,
        ),
        // Caption · labelSmall · 11 / 400 · Inter
        labelSmall = TextStyle(
            fontFamily = Inter,
            fontWeight = FontWeight.Normal,
            fontSize = 11.sp,
            lineHeight = 16.sp,
        ),
    )
}
