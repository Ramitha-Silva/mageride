package lk.mageride.passenger.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf

// The app's one theme. D2' §0.2 is authoritative; this file is the Compose binding of it.
//
// **No dynamic colour.** M3's `dynamicLightColorScheme` would hand the app the handset
// wallpaper's palette on Android 12+, and §0.2 is explicit that the token table is the single
// source of truth shared with Figma, SwiftUI and the Tailwind preset. It would also break MAP-03:
// the vehicle legend is a fixed eleven-colour identity, and a scheme whose primary had turned
// green would put the app's own accent inside that legend. Not a preference — do not add it
// without a micro-change-set.

private val LightScheme: ColorScheme = lightColorScheme(
    primary = MageRideLight.Primary,
    onPrimary = MageRideLight.OnPrimary,
    primaryContainer = MageRideLight.PrimaryContainer,
    onPrimaryContainer = MageRideLight.OnPrimaryContainer,
    secondary = MageRideLight.Secondary,
    onSecondary = MageRideLight.OnPrimary,
    secondaryContainer = MageRideLight.SecondaryContainer,
    onSecondaryContainer = MageRideLight.Secondary,
    background = MageRideLight.Background,
    onBackground = MageRideLight.OnSurface,
    surface = MageRideLight.Surface,
    onSurface = MageRideLight.OnSurface,
    surfaceVariant = MageRideLight.SurfaceVariant,
    onSurfaceVariant = MageRideLight.OnSurfaceVariant,
    outline = MageRideLight.Outline,
    outlineVariant = MageRideLight.OutlineVariant,
    error = MageRideLight.Error,
    onError = MageRideLight.OnPrimary,
)

private val DarkScheme: ColorScheme = darkColorScheme(
    primary = MageRideDark.Primary,
    onPrimary = MageRideDark.OnPrimary,
    primaryContainer = MageRideDark.PrimaryContainer,
    onPrimaryContainer = MageRideDark.OnPrimaryContainer,
    secondary = MageRideDark.Secondary,
    onSecondary = MageRideDark.SecondaryContainer,
    secondaryContainer = MageRideDark.SecondaryContainer,
    onSecondaryContainer = MageRideDark.Secondary,
    background = MageRideDark.Background,
    onBackground = MageRideDark.OnSurface,
    surface = MageRideDark.Surface,
    onSurface = MageRideDark.OnSurface,
    surfaceVariant = MageRideDark.SurfaceVariant,
    onSurfaceVariant = MageRideDark.OnSurfaceVariant,
    outline = MageRideDark.Outline,
    outlineVariant = MageRideDark.OutlineVariant,
    error = MageRideDark.Error,
    onError = MageRideDark.OnPrimary,
)

private val LightStatus = StatusColors(
    success = MageRideLight.Success,
    warning = MageRideLight.Warning,
    onStatus = MageRideLight.OnPrimary,
)

private val DarkStatus = StatusColors(
    success = MageRideDark.Success,
    warning = MageRideDark.Warning,
    onStatus = MageRideDark.OnPrimaryContainer,
)

private val LocalSpacing = staticCompositionLocalOf { Spacing() }
private val LocalRadius = staticCompositionLocalOf { Radius() }
private val LocalElevation = staticCompositionLocalOf { Elevation() }
private val LocalStatusColors = staticCompositionLocalOf { LightStatus }
private val LocalVehicleColors = staticCompositionLocalOf { VehicleColors.Legend }
private val LocalModeColors = staticCompositionLocalOf { ModeColors.Badges }

/**
 * The MageRide tokens M3 has no slot for.
 *
 * Mirrors `MaterialTheme`'s own accessor shape so a screen reads
 * `MageRideTheme.spacing.md` beside `MaterialTheme.colorScheme.primary` without a seam.
 */
internal object MageRideTheme {
    internal val spacing: Spacing
        @Composable @ReadOnlyComposable
        get() = LocalSpacing.current

    internal val radius: Radius
        @Composable @ReadOnlyComposable
        get() = LocalRadius.current

    internal val elevation: Elevation
        @Composable @ReadOnlyComposable
        get() = LocalElevation.current

    /** `success` / `warning` — see [StatusColors]. */
    internal val status: StatusColors
        @Composable @ReadOnlyComposable
        get() = LocalStatusColors.current

    /** MAP-03's per-vehicle-type marker legend. */
    internal val vehicle: VehicleColors
        @Composable @ReadOnlyComposable
        get() = LocalVehicleColors.current

    /** Mode A/B/C badge colours. */
    internal val mode: ModeColors
        @Composable @ReadOnlyComposable
        get() = LocalModeColors.current
}

/**
 * Wraps [content] in the MageRide design system.
 *
 * Every composable in this app — every screen group's, not just the shell's — must be inside
 * exactly one of these. `MainActivity` puts it directly under `setContent`.
 *
 * @param darkTheme Follows the system by default. Taking it as a parameter is what lets a
 *   `@Preview` render both appearances, which is the only way the dark tokens get looked at
 *   before a passenger finds them at night.
 */
@Composable
internal fun MageRideTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    CompositionLocalProvider(
        LocalSpacing provides Spacing(),
        LocalRadius provides Radius(),
        LocalElevation provides Elevation(),
        LocalStatusColors provides if (darkTheme) DarkStatus else LightStatus,
        LocalVehicleColors provides VehicleColors.Legend,
        LocalModeColors provides ModeColors.Badges,
    ) {
        MaterialTheme(
            colorScheme = if (darkTheme) DarkScheme else LightScheme,
            typography = MageRideTypography,
            shapes = MageRideShapes,
            content = content,
        )
    }
}
