package lk.mageride.passenger.ui.theme

import androidx.compose.ui.graphics.Color

// D2' §0.2 — "AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI".
//
// Transcribed literally, light hex then dark hex, in the order the spec's table prints them.
// Nothing here is a taste judgement and nothing may be "adjusted to look right": the same hexes
// are `apps/driver-android`'s, the iOS asset catalogues' (C085/C094) and the Tailwind preset's
// (AL-52), and a value that drifts here makes the surfaces disagree in a way only a screenshot
// review can catch. They are also the `passenger_android.html` wireframe's own `:root` custom
// properties. `ThemeTokensTest` asserts every one of them against the table.
//
// **Duplicated from the driver app on purpose.** The two are separate Gradle modules with
// separate APKs and neither depends on the other; the shared thing is the SPEC, not a library.
// A `:design-system` module would be a third place for §0.2 to be wrong.

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
 * else — and they carry meaning the app cannot express otherwise: a Mode B subscription is "Paid"
 * in `success` and "Pending verification" in `warning`, and a payment is Succeeded or Pending in
 * the same pair. Reached through [MageRideTheme.status] rather than `MaterialTheme.colorScheme`,
 * so it is obvious at the call site that these are ours.
 *
 * @property success Paid, confirmed, delivered.
 * @property warning Pending, awaiting verification, degraded.
 * @property onStatus Label colour that meets contrast on either of the two above.
 */
internal data class StatusColors(val success: Color, val warning: Color, val onStatus: Color)

/**
 * MAP-03's vehicle-type legend — the marker colour for each of the eleven canonical types.
 *
 * D2' §0.2 fixes these as one table for markers *and* tier cards, so a three-wheeler is the same
 * yellow on the live map, in SCR-PA-006's filter chips and on SCR-PA-009's booking tiers.
 * **Train is deliberately a distinct red with a rail icon**: §0.2 calls that out because a bus and
 * a train sharing a green `directions_bus` is the one confusion Mode A cannot afford.
 *
 * The same hexes are what `passenger_android.html` uses in its `--veh*` custom properties.
 *
 * **This app is where the whole table is actually load-bearing.** The driver home map draws one
 * vehicle and tints it with a single `icon-color` (AL-31); the passenger map draws every mode in
 * a cell at once, so the legend reaches MapLibre as a `match` expression —
 * [lk.mageride.passenger.map.VehicleLayers.markerColourExpression].
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
 * @property modeB Private/school transport subscriptions, visible only to an entitled passenger.
 * @property modeC On-demand hire.
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
 * SCR-PA-028's call palette.
 *
 * **A screen that is not on the M3 colour scheme, and has to be.** `passenger_android.html` draws
 * the call on a dark surface, and a call that switched to white in daylight would be a different
 * screen twice a day. A phone call has one appearance.
 *
 * `connected` is D2' §0.2's **dark** `secondary` and `background` is close to its dark
 * `background`; they are transcribed rather than aliased to the dark scheme, because this screen
 * is dark in the *light* theme too and reading it out of [MageRideDark] would make it change when
 * that table did. The driver app reached the same conclusion for SCR-DA-031.
 *
 * @property background The `screen` the whole call sits on.
 * @property surface The avatar disc, and the mute / speaker actions.
 * @property onCall The name and the dialled state, at full contrast.
 * @property hint *"In-app call"*, and the avatar glyph.
 * @property connected The one line that says media is flowing.
 */
internal object CallColors {
    val background: Color = Color(0xFF15171B)
    val surface: Color = Color(0xFF2A2D31)
    val onCall: Color = Color(0xFFFFFFFF)
    val hint: Color = Color(0xFFAEB3BC)
    val connected: Color = Color(0xFF9FCAFF)
}

/**
 * SCR-PA-029's palette.
 *
 * The wireframe draws the passenger SOS on `#2A0A0A` with a `#3A1414` contact card outlined in
 * `#5A2020`, an `#FFB4AB` status line and the §0.2 `error` disc inside a translucent halo of
 * itself (`box-shadow: 0 0 0 14px rgba(211,47,47,.25)`). Dark in both appearances, like
 * [CallColors]: an alarm screen that could be mistaken for an ordinary one is the failure mode
 * this palette exists to prevent.
 *
 * @property background The `screen`.
 * @property surface The emergency-contact card.
 * @property outline That card's border.
 * @property onSos Titles and card text.
 * @property hint *"Sending GPS + trip to emergency contacts via SMS…"*.
 * @property halo The ring around the SOS disc — the §0.2 `error` at 25%.
 */
internal object SosColors {
    val background: Color = Color(0xFF2A0A0A)
    val surface: Color = Color(0xFF3A1414)
    val outline: Color = Color(0xFF5A2020)
    val onSos: Color = Color(0xFFFFFFFF)
    val hint: Color = Color(0xFFFFB4AB)
    val halo: Color = Color(0x40D32F2F)
}

/** Map pin colours — D2' §0.3: "`pickup` green, `dropoff` red, `user` blue dot". */
internal object PinColors {
    val Pickup = Color(0xFF2E9E4F)
    val Dropoff = Color(0xFFD32F2F)
    val User = Color(0xFF1E6FE5)
}
