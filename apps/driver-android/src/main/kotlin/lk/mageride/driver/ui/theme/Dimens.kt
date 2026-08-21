package lk.mageride.driver.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Shapes
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

// D2' §0.2 — spacing, corner radius, elevation and the CTA token.
//
// M3 has a `Shapes` slot but no spacing or elevation scale, so the three live side by side here
// and reach a composable through `MageRideTheme.spacing` / `.radius` / `.elevation`. Screens use
// the token, never a raw dp: the 4 px grid is what keeps eight screen groups written by eight
// sessions looking like one app.

/**
 * §0.2's "Spacing (4px base grid): `4, 8, 12, 16, 24, 32, 48` → tokens `xxs/xs/sm/md/lg/xl/xxl`".
 */
internal data class Spacing(
    val xxs: Dp = 4.dp,
    val xs: Dp = 8.dp,
    val sm: Dp = 12.dp,
    val md: Dp = 16.dp,
    val lg: Dp = 24.dp,
    val xl: Dp = 32.dp,
    val xxl: Dp = 48.dp,
)

/**
 * §0.2's "Corner radius: `sm 8` (buttons, chips) · `md 12` (fields, sheets-top) · `lg 16`
 * (modals) · `card 24` (elevated cards, bottom sheets)".
 */
internal data class Radius(val sm: Dp = 8.dp, val md: Dp = 12.dp, val lg: Dp = 16.dp, val card: Dp = 24.dp)

/**
 * §0.2's "Elevation: Android = M3 levels `0/1/3/6/8/12dp` (`surfaceColorAtElevation`)".
 *
 * The names are M3's own level numbers rather than semantic ones, because that is how the spec
 * prints them and how `surfaceColorAtElevation` is called.
 */
internal data class Elevation(
    val level0: Dp = 0.dp,
    val level1: Dp = 1.dp,
    val level2: Dp = 3.dp,
    val level3: Dp = 6.dp,
    val level4: Dp = 8.dp,
    val level5: Dp = 12.dp,
)

/**
 * The M3 shape scale, cut to §0.2's four radii.
 *
 * `extraSmall`/`small` are the chip and button `sm 8`; `medium` the field and sheet-top `md 12`;
 * `large` the modal `lg 16`; `extraLarge` the card and bottom-sheet `card 24`.
 */
internal val MageRideShapes: Shapes = Shapes(
    extraSmall = RoundedCornerShape(8.dp),
    small = RoundedCornerShape(8.dp),
    medium = RoundedCornerShape(12.dp),
    large = RoundedCornerShape(16.dp),
    extraLarge = RoundedCornerShape(24.dp),
)

/**
 * §0.2's CTA token — "height `56dp`, radius `sm 8`, `primary` bg, `onPrimary` label
 * `titleMedium`, optional 20dp leading/trailing icon".
 *
 * It "replaces NY `PrimaryButton`", so it is one shape with one height across every screen group;
 * the composable that applies it is `ui/component/MageRideCta.kt`.
 */
internal object CtaTokens {
    val Height: Dp = 56.dp
    val Radius: Dp = 8.dp
    val IconSize: Dp = 20.dp
}

/**
 * Fixed control sizes §0.2 does not put on the 4 px grid.
 *
 * A hairline border, a paging dot and an OTP cell are **measurements of one control**, not
 * spacing between two, so they cannot be expressed as `Spacing` without lying about what the
 * number means. They live here rather than at their call sites for the same reason the spacing
 * scale does: eight screen groups written by eight sessions have to draw the same box.
 *
 * Added by C068 for the cluster-1 wireframes (`specs/wireframes/driver_android.html`); anything a
 * later group needs at a new size belongs here too, not inline.
 */
internal object ControlTokens {

    /** `1.5px` outline in the wireframe's unselected language box, rounded to the display grid. */
    val Border: Dp = 1.dp

    /** The same box once it is selected — the wireframe thickens it rather than only recolouring. */
    val BorderSelected: Dp = 2.dp

    /** Leading icon inside a list row. Matches the CTA's own icon token. */
    val RowIcon: Dp = 20.dp

    /** The ⚑ inside an inline chip, which sits on `labelSmall` rather than on a row. */
    val ChipIcon: Dp = 14.dp

    /** Carousel paging dot, and its current-slide form. */
    val Dot: Dp = 6.dp
    val DotActive: Dp = 8.dp

    /** The wireframe's `illus` panel on SCR-DA-002 and its icon. */
    val IllustrationPanel: Dp = 96.dp
    val IllustrationIcon: Dp = 40.dp

    /** SCR-DA-003a's 📷 capture tile, and the avatar with its camera badge. */
    val CaptureTile: Dp = 88.dp
    val Avatar: Dp = 96.dp
    val AvatarBadge: Dp = 32.dp

    /** One cell of SCR-DA-003's six-digit OTP entry. */
    val OtpCell: Dp = 44.dp

    /** SCR-DA-005's shutter (C069). A camera's shutter is a size, not a spacing. */
    val Shutter: Dp = 64.dp

    /** SCR-DA-005's crop quad: its outline, its rule-of-thirds grid and a corner handle. */
    val ScannerQuadStroke: Dp = 2.dp
    val ScannerGridStroke: Dp = 1.dp
    val ScannerHandle: Dp = 10.dp

    /** SCR-DA-004a…004c's wide capture panel — the wireframe's `illus` at `height:120px`. */
    val CapturePanel: Dp = 120.dp

    /** The wireframe's status dot on a My Vehicles row (`cdot`). */
    val StatusDot: Dp = 12.dp

    /** SCR-DA-006's ⏳ verdict avatar. */
    val StatusAvatar: Dp = 60.dp

    /** SCR-DA-001's white app mark, its corner and the spinner under it. */
    val SplashMark: Dp = 84.dp
    val SplashMarkRadius: Dp = 22.dp
    val SplashSpinner: Dp = 28.dp

    // ---- C070 · dashboard, dispatch, active ride --------------------------------------

    /** The grab handle on every bottom sheet the wireframe draws (`.handle`, `34 x 4`). */
    val SheetHandleWidth: Dp = 32.dp
    val SheetHandleHeight: Dp = 4.dp

    /** SCR-DA-010's `.bigtoggle` — the ONLINE / OFFLINE bar, taller than a CTA on purpose. */
    val BigToggle: Dp = 64.dp

    /**
     * The floor under SCR-DA-010's map, which sizes itself from what the viewport has left.
     *
     * Not a wireframe measurement — the wireframe's map is `flex:1` and has no floor because CSS
     * flex cannot go negative. Compose can: a short handset carrying a five-row banner stack and a
     * Mode A/B journey sheet leaves a remainder that is zero or less, and a map measured to that is
     * a map the driver cannot see at all. This is the smallest rectangle still worth calling a map.
     */
    val HomeMapMinimum: Dp = 160.dp

    /** SCR-DA-014's 15-second countdown ring (`.ring`, `90px`) and its stroke. */
    val CountdownRing: Dp = 96.dp
    val CountdownStroke: Dp = 8.dp

    /** SCR-DA-013's inline map preview (`.map` at `flex:0 0 120px`). */
    val MapPreview: Dp = 120.dp

    /** SCR-DA-015's rider avatar (`.avatar`, `40px`) — the large one is Profile Setup's. */
    val AvatarSmall: Dp = 40.dp

    /** SCR-DA-036's modal navigation drawer (`width:250px`, on M3's 280 dp standard sheet). */
    val DrawerWidth: Dp = 280.dp

    // ---- C071 · the delivery sheets ----------------------------------------------------

    /**
     * SCR-DA-016a/c's 📞 button beside a sender or recipient row (`.btn-out`, `46 x 38`).
     *
     * Rounded onto the 4 px grid, the same way C068 rounded the wireframe's `1.5px` outline: the
     * numbers in the HTML are CSS pixels and the grid is what keeps the row the same height as
     * every other control in the sheet.
     */
    val CallButtonWidth: Dp = 48.dp
    val CallButtonHeight: Dp = 40.dp

    // ---- C072 · jobs, level, earnings --------------------------------------------------

    /** SCR-DA-019's Driver Level badge — the wireframe's `96 x 96` `primaryContainer` square. */
    val LevelBadge: Dp = 96.dp

    /** SCR-DA-019's points bar (`.progress`, `height:8px`), which M3 draws at 4 dp by default. */
    val LevelProgress: Dp = 8.dp

    /**
     * The gap M3 1.3+ leaves between a `LinearProgressIndicator`'s track and its fill.
     *
     * Zero, because the wireframe's bar is one continuous rounded rail — the same reason its stop
     * indicator is drawn away. A token rather than a literal so the value is arguable in one place.
     */
    val ProgressGap: Dp = 0.dp

    /** SCR-DA-020's trend chart (`.bars`, `height:90px`), rounded onto the 4 px grid. */
    val EarningsChart: Dp = 88.dp

    // ---- C075 · the call, the SOS, the support list and the alerts ---------------------

    /** SCR-DA-031's mute and speaker controls — the wireframe's `.fab`, `44 x 44`. */
    val CallAction: Dp = 44.dp

    /** Its red hang-up disc (`width:64px;height:64px;border-radius:50%`). */
    val CallEnd: Dp = 64.dp

    /**
     * SCR-DA-032's SOS disc (`128px`) and the translucent ring around it (`box-shadow: … 14px`).
     *
     * Rounded onto the 4 px grid the same way C068 rounded the wireframe's `1.5px` outline. It is
     * the largest control in the app on purpose: it is pressed by someone who is not looking.
     */
    val SosButton: Dp = 128.dp
    val SosHalo: Dp = 16.dp

    /** SCR-DA-033's `🔍 Search help` field (`.searchbar`, `height:48px`). */
    val SearchBar: Dp = 48.dp

    /**
     * The leading glyph square on a list row (`.listrow .ic`, `30 x 30`, `radius 8`).
     *
     * SCR-DA-034 draws one per alert and colours it by kind; SCR-DA-036's drawer rows use the same
     * square. Distinct from [RowIcon], which is the *icon* inside it.
     */
    val ListRowIcon: Dp = 32.dp
}
