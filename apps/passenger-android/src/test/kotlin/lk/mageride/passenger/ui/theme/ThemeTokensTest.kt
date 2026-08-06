package lk.mageride.passenger.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * D2' §0.2 is *"AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI"*, and this
 * is the assertion that keeps this app's half of that promise honest.
 *
 * Every expectation below is typed from the spec's own table rather than read back out of the
 * production object, which is the only way a test of a constant is worth anything: if `Color.kt`
 * and the table disagree, exactly one of them is wrong and this says which.
 *
 * The same hexes are `apps/driver-android`'s, the iOS asset catalogues' (C085/C094) and the
 * Tailwind preset's (AL-52). A drift here would show up as surfaces that look almost the same,
 * which is the hardest kind of inconsistency to find in review — and on the vehicle legend it
 * would be worse than cosmetic: a driver and their passenger would be looking at the same tuk in
 * two different colours.
 */
class ThemeTokensTest {

    @Test
    fun the_light_colour_roles_match_the_d2_0_2_table() {
        assertEquals(Color(0xFFFF6D00), MageRideLight.Primary, "primary")
        assertEquals(Color(0xFFFFFFFF), MageRideLight.OnPrimary, "onPrimary")
        assertEquals(Color(0xFFFFE0CC), MageRideLight.PrimaryContainer, "primaryContainer")
        assertEquals(Color(0xFF2B1100), MageRideLight.OnPrimaryContainer, "onPrimaryContainer")
        assertEquals(Color(0xFF0061A4), MageRideLight.Secondary, "secondary")
        assertEquals(Color(0xFFD1E4FF), MageRideLight.SecondaryContainer, "secondaryContainer")
        assertEquals(Color(0xFFFFFFFF), MageRideLight.Background, "background")
        assertEquals(Color(0xFFF7F8FA), MageRideLight.Surface, "surface")
        assertEquals(Color(0xFFECEEF1), MageRideLight.SurfaceVariant, "surfaceVariant")
        assertEquals(Color(0xFFC7CBD1), MageRideLight.Outline, "outline")
        assertEquals(Color(0xFF1A1C1E), MageRideLight.OnSurface, "onSurface")
        assertEquals(Color(0xFF44474B), MageRideLight.OnSurfaceVariant, "onSurfaceVariant")
        assertEquals(Color(0xFF74777C), MageRideLight.OutlineVariant, "outlineVariant")
        assertEquals(Color(0xFF2E9E4F), MageRideLight.Success, "success")
        assertEquals(Color(0xFFF5A300), MageRideLight.Warning, "warning")
        assertEquals(Color(0xFFD32F2F), MageRideLight.Error, "error")
    }

    @Test
    fun the_dark_colour_roles_match_the_d2_0_2_table() {
        assertEquals(Color(0xFFFFB68A), MageRideDark.Primary, "primary")
        assertEquals(Color(0xFF4A2300), MageRideDark.OnPrimary, "onPrimary")
        assertEquals(Color(0xFF6A3500), MageRideDark.PrimaryContainer, "primaryContainer")
        assertEquals(Color(0xFFFFDCC4), MageRideDark.OnPrimaryContainer, "onPrimaryContainer")
        assertEquals(Color(0xFF9FCAFF), MageRideDark.Secondary, "secondary")
        assertEquals(Color(0xFF00497D), MageRideDark.SecondaryContainer, "secondaryContainer")
        assertEquals(Color(0xFF121316), MageRideDark.Background, "background")
        assertEquals(Color(0xFF1A1C1E), MageRideDark.Surface, "surface")
        assertEquals(Color(0xFF2A2D31), MageRideDark.SurfaceVariant, "surfaceVariant")
        assertEquals(Color(0xFF43474E), MageRideDark.Outline, "outline")
        assertEquals(Color(0xFFE3E2E6), MageRideDark.OnSurface, "onSurface")
        assertEquals(Color(0xFFC3C7CF), MageRideDark.OnSurfaceVariant, "onSurfaceVariant")
        assertEquals(Color(0xFF8D9199), MageRideDark.OutlineVariant, "outlineVariant")
        assertEquals(Color(0xFF7FD89A), MageRideDark.Success, "success")
        assertEquals(Color(0xFFFFCF6B), MageRideDark.Warning, "warning")
        assertEquals(Color(0xFFFFB4AB), MageRideDark.Error, "error")
    }

    @Test
    fun the_map_03_vehicle_legend_matches_the_d2_0_2_table() {
        val legend = VehicleColors.Legend
        assertEquals(Color(0xFF2E9E4F), legend.bus, "vehBus")
        // §0.2 calls the rail colour out explicitly — a train sharing the bus green is the one
        // confusion Mode A cannot afford, and this is the app that draws both at once.
        assertEquals(Color(0xFFE5331F), legend.train, "vehTrain")
        assertEquals(Color(0xFF8E44CE), legend.motorbike, "vehMotorbike")
        assertEquals(Color(0xFFF5C518), legend.threeWheeler, "vehTuk")
        assertEquals(Color(0xFF1ABC9C), legend.flex, "vehFlex")
        assertEquals(Color(0xFF1E6FE5), legend.sedan, "vehSedan")
        assertEquals(Color(0xFFEC4899), legend.miniVan, "vehMiniVan")
        assertEquals(Color(0xFFF57C00), legend.van, "vehVan")
        assertEquals(Color(0xFF8B5E3C), legend.truck, "vehTruck")
        assertEquals(Color(0xFF808000), legend.miniTruck, "vehMiniTruck")
        assertEquals(Color(0xFF8A8F98), legend.private, "vehPrivate")
    }

    @Test
    fun the_mode_badges_match_the_d2_0_2_table() {
        assertEquals(Color(0xFF2E9E4F), ModeColors.Badges.modeA, "Mode A green")
        assertEquals(Color(0xFF6B7280), ModeColors.Badges.modeB, "Mode B grey")
        assertEquals(Color(0xFFFF6D00), ModeColors.Badges.modeC, "Mode C orange")
    }

    @Test
    fun the_0_3_pin_colours_are_pickup_green_dropoff_red_user_blue() {
        // "Pins: `pickup` green, `dropoff` red, `user` blue dot" — §0.3, verbatim. Green and red
        // are §0.2's `success` and `error`; the user dot is `vehSedan` blue, which is the only
        // blue the token table has that is not a text or container role.
        assertEquals(Color(0xFF2E9E4F), PinColors.Pickup, "pickup")
        assertEquals(Color(0xFFD32F2F), PinColors.Dropoff, "dropoff")
        assertEquals(Color(0xFF1E6FE5), PinColors.User, "user")
    }

    @Test
    fun the_type_scale_matches_the_d2_0_2_table() {
        // Role | token | px | weight — the four columns of §0.2's typography table.
        assertEquals(32f, MageRideTypography.displaySmall.fontSize.value, "Display size")
        assertEquals(FontWeight.Bold, MageRideTypography.displaySmall.fontWeight, "Display weight")

        assertEquals(22f, MageRideTypography.headlineMedium.fontSize.value, "Headline size")
        assertEquals(FontWeight.Bold, MageRideTypography.headlineMedium.fontWeight, "Headline weight")

        assertEquals(18f, MageRideTypography.titleLarge.fontSize.value, "Title size")
        assertEquals(FontWeight.SemiBold, MageRideTypography.titleLarge.fontWeight, "Title weight")

        assertEquals(16f, MageRideTypography.titleMedium.fontSize.value, "Subtitle size")
        assertEquals(FontWeight.SemiBold, MageRideTypography.titleMedium.fontWeight, "Subtitle weight")

        assertEquals(16f, MageRideTypography.bodyLarge.fontSize.value, "Body size")
        assertEquals(FontWeight.Normal, MageRideTypography.bodyLarge.fontWeight, "Body weight")

        assertEquals(14f, MageRideTypography.bodyMedium.fontSize.value, "Body small size")
        assertEquals(FontWeight.Normal, MageRideTypography.bodyMedium.fontWeight, "Body small weight")

        assertEquals(12f, MageRideTypography.labelMedium.fontSize.value, "Label size")
        assertEquals(FontWeight.Medium, MageRideTypography.labelMedium.fontWeight, "Label weight")

        assertEquals(11f, MageRideTypography.labelSmall.fontSize.value, "Caption size")
        assertEquals(FontWeight.Normal, MageRideTypography.labelSmall.fontWeight, "Caption weight")
    }

    @Test
    fun the_spacing_radius_and_elevation_scales_match_the_d2_0_2_table() {
        val spacing = Spacing()
        assertEquals(listOf(4f, 8f, 12f, 16f, 24f, 32f, 48f), spacing.asFloats(), "4px base grid")

        val radius = Radius()
        assertEquals(8f, radius.sm.value, "sm — buttons, chips")
        assertEquals(12f, radius.md.value, "md — fields, sheets-top")
        assertEquals(16f, radius.lg.value, "lg — modals")
        assertEquals(24f, radius.card.value, "card — elevated cards, bottom sheets")

        val elevation = Elevation()
        assertEquals(
            listOf(0f, 1f, 3f, 6f, 8f, 12f),
            listOf(
                elevation.level0.value,
                elevation.level1.value,
                elevation.level2.value,
                elevation.level3.value,
                elevation.level4.value,
                elevation.level5.value,
            ),
            "M3 levels 0/1/3/6/8/12dp",
        )
    }

    @Test
    fun the_cta_token_is_the_56dp_bar_the_spec_replaced_primarybutton_with() {
        assertEquals(56f, CtaTokens.Height.value, "CTA height")
        assertEquals(8f, CtaTokens.Radius.value, "CTA radius — the sm token")
        assertEquals(20f, CtaTokens.IconSize.value, "optional leading/trailing icon")
    }

    private fun Spacing.asFloats() = listOf(xxs.value, xs.value, sm.value, md.value, lg.value, xl.value, xxl.value)
}
