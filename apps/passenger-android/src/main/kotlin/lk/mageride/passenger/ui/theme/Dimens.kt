package lk.mageride.passenger.ui.theme

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
 * It "replaces NY `PrimaryButton`", so it is one shape with one height across every screen group.
 * The shell declares the token and ships no composable for it: the first screen group to draw a
 * CTA owns `ui/component/MageRideCta.kt`, the way C068 did on the driver side.
 */
internal object CtaTokens {
    val Height: Dp = 56.dp
    val Radius: Dp = 8.dp
    val IconSize: Dp = 20.dp
}

/**
 * Fixed control sizes §0.2 does not put on the 4 px grid.
 *
 * A drawer's width, a sheet's grab handle and a search bar's height are **measurements of one
 * control**, not spacing between two, so they cannot be expressed as [Spacing] without lying
 * about what the number means. They live here rather than at their call sites for the same reason
 * the spacing scale does: eight screen groups written by eight sessions have to draw the same box.
 *
 * **Only the shell's own controls are here.** The numbers are `passenger_android.html`'s, rounded
 * onto the 4 px display grid; C077–C084 append theirs as they take their screens, exactly as
 * C068–C075 did on the driver side.
 */
internal object ControlTokens {

    /** SCR-PA-033's modal navigation drawer (`width:248px`, on M3's 280 dp standard sheet). */
    val DrawerWidth: Dp = 280.dp

    /** The `.avatar` disc in the drawer header, and anywhere else a person is drawn small. */
    val Avatar: Dp = 40.dp

    /** The grab handle on every bottom sheet the wireframe draws (`.handle`). */
    val SheetHandleWidth: Dp = 32.dp
    val SheetHandleHeight: Dp = 4.dp

    /** The leading glyph square on a list row (`.listrow .ic`, `30 x 30`, `radius 8`). */
    val ListRowIcon: Dp = 32.dp

    /** The icon inside it, and a CTA's optional leading icon. */
    val RowIcon: Dp = 20.dp

    /** The offline banner's `📡` (`.banner`), which sits on `labelMedium` rather than on a row. */
    val BannerIcon: Dp = 20.dp

    /** `1.5px` outline in the wireframe's fields and outlined buttons, rounded to the grid. */
    val Border: Dp = 1.dp
    val BorderSelected: Dp = 2.dp

    // ---- C077 · the first-run cluster (SCR-PA-001…005) ---------------------------------

    /** SCR-PA-001's white app mark on the orange screen, its corner, and the loader under it. */
    val SplashMark: Dp = 84.dp
    val SplashMarkRadius: Dp = 22.dp
    val SplashSpinner: Dp = 32.dp

    /** SCR-PA-002/005's `illus` panel and the icon inside it. */
    val IllustrationPanel: Dp = 96.dp
    val IllustrationIcon: Dp = 40.dp

    /** The carousel's paging dot, and its current-slide form (`.dots i`, `7px` → the 4 px grid). */
    val Dot: Dp = 8.dp
    val DotActive: Dp = 20.dp

    /** One cell of SCR-PA-003's six-digit OTP entry (`.otp .b`, `38 x 46`). */
    val OtpCellWidth: Dp = 40.dp
    val OtpCellHeight: Dp = 48.dp

    /** The ⚑ inside an inline chip, which sits on `labelSmall` rather than on a row (C078). */
    val ChipIcon: Dp = 14.dp

    /** SCR-PA-007's `avatar` and SCR-PA-010's sheet handle grip (C078). */
    val AvatarSmall: Dp = 40.dp

    /** SCR-PA-010's `searchbar` (`height:48px`) and its bottom-sheet grab handle (C078). */
    val SearchBar: Dp = 48.dp
    val SheetHandleWidthLarge: Dp = 34.dp

    /**
     * The map inside a modal picker (C079's `MapPickSheet`).
     *
     * Tall enough to orient by, short enough to leave the form behind the sheet visible — a picker
     * that filled the screen would be a screen, and the wireframe assigns no id to one.
     */
    val SheetMap: Dp = 320.dp

    /** SCR-PA-014's radar sweep — the widening ring while dispatch looks for a driver (C080). */
    val RadarSize: Dp = 120.dp

    /** SCR-PA-017's viewfinder, and SCR-PA-019's tappable star (C080). */
    val Viewfinder: Dp = 280.dp
    val Star: Dp = 40.dp

    /** SCR-PA-004's `avatar lg` and the `＋` camera badge on it. */
    val AvatarLarge: Dp = 96.dp
    val AvatarBadge: Dp = 32.dp

    // ---- C083 · settings, addresses and the drawer header ------------------------------

    /**
     * SCR-PA-026's pin map, between the address rows and the CTA.
     *
     * The wireframe gives it `flex:0 0 100px` inside a 560 px phone — about a fifth of the screen.
     * Rounded **up** to 180 dp rather than transcribed: this map is a control, not an illustration,
     * and 100 px of a 640 dp handset leaves a pin-drop target a thumb covers entirely. It is still
     * a strip under the list rather than the full-bleed map SCR-PA-010 draws, which is the
     * proportion the wireframe is actually fixing.
     */
    val InlineMap: Dp = 180.dp

    // ---- C084 · the call, the alarm and support -----------------------------------------

    /**
     * SCR-PA-028's `🔇` and `🔊` discs, and the red `📞` that ends the call.
     *
     * The wireframe gives the end button `width:64px;height:64px;border-radius:50%` and draws the
     * two toggles as `.fab`s beside it. Both numbers are the driver app's for SCR-DA-031 as well —
     * one call screen on two surfaces should not have two button sizes.
     */
    val CallAction: Dp = 44.dp
    val CallEnd: Dp = 64.dp

    /**
     * SCR-PA-029's disc and the ring around it.
     *
     * `width:130px;height:130px;border-radius:50%` with `box-shadow: 0 0 0 14px rgba(211,47,47,.25)`
     * — rounded onto the 4 px grid as 128 + 16, which is what the driver's SCR-DA-032 uses.
     */
    val SosButton: Dp = 128.dp
    val SosHalo: Dp = 16.dp

    /** SCR-PA-031's `⬆️` above *"Update required"*, and SCR-PA-028's avatar glyph. */
    val DialogIcon: Dp = 32.dp
}
