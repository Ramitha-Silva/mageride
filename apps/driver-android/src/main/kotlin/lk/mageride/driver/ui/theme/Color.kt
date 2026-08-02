package lk.mageride.driver.ui.theme

import androidx.compose.ui.graphics.Color

// D2' §0.2 — "AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI".
//
// Transcribed literally, light hex then dark hex, in the order the spec's table prints them.
// Nothing here is a taste judgement and nothing may be "adjusted to look right": the same hexes
// are the iOS asset catalogue's (C085) and the Tailwind preset's (AL-52), and a value that drifts
// here makes the three surfaces disagree in a way only a screenshot review can catch.
// `ThemeTokensTest` asserts every one of them against the table.

/** The D2' §0.2 brand and semantic roles, light appearance. */
internal object MageRideLight {
    val Primary = Color(0xFFFF6D00)
    val OnPrimary = Color(0xFFFFFFFF)
    val PrimaryContainer = Color(0xFFFFE0CC)
    val OnPrimaryContainer = Color(0xFF2B1100)
    val Secondary = Color(0xFF0061A4)
    val SecondaryContainer = Color(0xFFD1E4FF)
    val Background = Color(0xFFFFFFFF)
    val Surface = Color(0xFFF7F8FA)
    val SurfaceVariant = Color(0xFFECEEF1)
    val Outline = Color(0xFFC7CBD1)
    val OnSurface = Color(0xFF1A1C1E)
    val OnSurfaceVariant = Color(0xFF44474B)
    val OutlineVariant = Color(0xFF74777C)
    val Success = Color(0xFF2E9E4F)
    val Warning = Color(0xFFF5A300)
    val Error = Color(0xFFD32F2F)
}

/** The same roles, dark appearance. §0.4 records these as the resolution of a Phase-A `[UNVERIFIED]`. */
internal object MageRideDark {
    val Primary = Color(0xFFFFB68A)
    val OnPrimary = Color(0xFF4A2300)
    val PrimaryContainer = Color(0xFF6A3500)
    val OnPrimaryContainer = Color(0xFFFFDCC4)
    val Secondary = Color(0xFF9FCAFF)
    val SecondaryContainer = Color(0xFF00497D)
    val Background = Color(0xFF121316)
    val Surface = Color(0xFF1A1C1E)
    val SurfaceVariant = Color(0xFF2A2D31)
    val Outline = Color(0xFF43474E)
    val OnSurface = Color(0xFFE3E2E6)
    val OnSurfaceVariant = Color(0xFFC3C7CF)
    val OutlineVariant = Color(0xFF8D9199)
    val Success = Color(0xFF7FD89A)
    val Warning = Color(0xFFFFCF6B)
    val Error = Color(0xFFFFB4AB)
}

/**
 * The three roles Material 3 has no slot for.
 *
 * `success` and `warning` are MageRide's, not M3's — the framework ships `error` and nothing
 * else — and they carry meaning the app cannot express otherwise: the daily-fee chip is
 * "PAID" in `success` and "DUE" in `warning`, and a verification row is Verified or Pending in
 * the same pair. Reached through [MageRideTheme.status] rather than `MaterialTheme.colorScheme`,
 * so it is obvious at the call site that these are ours.
 *
 * @property success Verified, paid, online.
 * @property warning Pending, due, degraded.
 * @property onStatus Label colour that meets contrast on either of the two above.
 */
internal data class StatusColors(val success: Color, val warning: Color, val onStatus: Color)

/**
 * MAP-03's vehicle-type legend — the marker colour for each of the eleven canonical types.
 *
 * D2' §0.2 fixes these as one table for markers *and* tier cards, so a three-wheeler is the same
 * yellow on the map, in the vehicle picker and on the fleet list. **Train is deliberately a
 * distinct red with a rail icon**: §0.2 calls that out because a bus and a train sharing a green
 * `directions_bus` is the one confusion Mode A cannot afford.
 *
 * The same hexes are what `driver_android.html` uses in its `--veh*` custom properties.
 */
@Suppress("LongParameterList") // Eleven vehicle types, eleven colours — the count is MAP-03's.
internal data class VehicleColors(
    val bus: Color,
    val train: Color,
    val motorbike: Color,
    val threeWheeler: Color,
    val flex: Color,
    val sedan: Color,
    val miniVan: Color,
    val van: Color,
    val truck: Color,
    val miniTruck: Color,
    val private: Color,
) {
    internal companion object {
        /**
         * The legend. One appearance only — a marker colour that changed between light and dark
         * would stop being an identity, and §0.2 prints a single hex per vehicle.
         */
        val Legend = VehicleColors(
            bus = Color(0xFF2E9E4F),
            train = Color(0xFFE5331F),
            motorbike = Color(0xFF8E44CE),
            threeWheeler = Color(0xFFF5C518),
            flex = Color(0xFF1ABC9C),
            sedan = Color(0xFF1E6FE5),
            miniVan = Color(0xFFEC4899),
            van = Color(0xFFF57C00),
            truck = Color(0xFF8B5E3C),
            miniTruck = Color(0xFF808000),
            private = Color(0xFF8A8F98),
        )
    }
}

/**
 * D2' §0.2's mode badges: Mode A green, Mode B grey, Mode C orange.
 *
 * @property modeA Scheduled public transport — bus and train (R-01).
 * @property modeB Private/school transport subscriptions.
 * @property modeC On-demand hire, the surface this app spends most of its life in.
 */
internal data class ModeColors(val modeA: Color, val modeB: Color, val modeC: Color) {
    internal companion object {
        val Badges = ModeColors(
            modeA = Color(0xFF2E9E4F),
            modeB = Color(0xFF6B7280),
            modeC = Color(0xFFFF6D00),
        )
    }
}

/**
 * SCR-DA-005's viewfinder palette (C069).
 *
 * **The one screen in this app that is not on the M3 colour scheme, and it has to be.** A camera
 * preview under a light surface is a light leak on every side of the document, so `driver_android
 * .html` draws the scanner on `#0f1115` with the `#FFB68A` primary tint on its actions and a grey
 * for the hint line. Those three hexes are the wireframe's; they live here rather than in the
 * screen because this file is where a colour is allowed to be a number.
 *
 * One appearance, not two: a viewfinder does not have a light theme.
 *
 * @property background The `screen` behind the camera surface.
 * @property accent `Use photo ›`, the flash toggle, the crop quad and its handles.
 * @property hint The capture hint and a disabled action on the capture bar.
 */
internal object ScannerColors {
    val background: Color = Color(0xFF0F1115)
    val accent: Color = Color(0xFFFFB68A)
    val hint: Color = Color(0xFF9AA0A6)
}

/** Map pin colours — D2' §0.3: "`pickup` green, `dropoff` red, `user` blue dot". */
internal object PinColors {
    val Pickup = Color(0xFF2E9E4F)
    val Dropoff = Color(0xFFD32F2F)
    val User = Color(0xFF1E6FE5)
}
