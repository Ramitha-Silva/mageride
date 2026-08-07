import SwiftUI

// D2' §0.2 — spacing, corner radius, elevation and the fixed control sizes, transcribed for SwiftUI.
//
// The numbers are the same as `apps/passenger-android/.../ui/theme/Dimens.kt` and
// `apps/driver-ios`'s, because they are the same design contract; what differs is the elevation row,
// and only because the spec says so: "Android = M3 levels 0/1/3/6/8/12dp
// (surfaceColorAtElevation); **iOS = subtle shadows (radius 8, y 2, opacity 0.12) + material blur**".
// Android tints a surface, iOS casts a shadow — the same intent in each platform's own vocabulary,
// which is Section C's whole subject.
//
// A view uses the token, never a raw number: the 4pt grid is what keeps eight screen groups written
// by eight sessions looking like one app. `ThemeTokenTests` holds the table.

/// §0.2's "Spacing (4px base grid): `4, 8, 12, 16, 24, 32, 48` → tokens `xxs/xs/sm/md/lg/xl/xxl`".
enum MageRideSpacing {
    static let xxs: CGFloat = 4
    static let xs: CGFloat = 8
    static let sm: CGFloat = 12
    static let md: CGFloat = 16
    static let lg: CGFloat = 24
    static let xl: CGFloat = 32
    static let xxl: CGFloat = 48
}

/// §0.2's "Corner radius: `sm 8` (buttons, chips) · `md 12` (fields, sheets-top) · `lg 16`
/// (modals) · `card 24` (elevated cards, bottom sheets)".
enum MageRideRadius {
    static let sm: CGFloat = 8
    static let md: CGFloat = 12
    static let lg: CGFloat = 16
    static let card: CGFloat = 24
}

/// §0.2's iOS elevation: "subtle shadows (`radius 8, y 2, opacity 0.12`) + material blur".
///
/// One shadow, not a six-level ladder. That is not a simplification of the Android row — it is what
/// the spec prints for this platform, and it is why a raised iOS surface is a `.shadow` plus a
/// `.background(.regularMaterial)` rather than a tinted fill.
enum MageRideElevation {
    static let shadowRadius: CGFloat = 8
    static let shadowOffsetY: CGFloat = 2
    static let shadowOpacity: Double = 0.12
}

/// Fixed control sizes §0.2 does not put on the 4pt grid.
///
/// A hairline border or a grabber is a **measurement of one control**, not spacing between two, so
/// it cannot be expressed as ``MageRideSpacing`` without lying about what the number means. The
/// values here are `specs/wireframes/passenger_ios.html`'s, rounded onto the grid where the HTML
/// uses a CSS pixel that is not on it.
///
/// **Deliberately short.** The driver app's counterpart has grown twenty-odd entries, one cluster at
/// a time, each measuring an SCR-DI-* cell; this holds the shell's own controls and nothing else.
/// C095–C102 append theirs, as C077–C084 did to `ControlTokens` on the Android side, rather than
/// putting a number at a call site.
enum MageRideControl {

    /// §0.2's CTA token — "height `56dp`, radius `sm 8`, `primary` bg, `onPrimary` label
    /// `titleMedium`, optional 20dp leading/trailing icon".
    ///
    /// **The wireframe draws it at `height:50px; border-radius:13px`**, which is the HIG's own
    /// button metrics rather than the token's. §0.2's CTA row is not marked as a platform delta and
    /// Section C does not list one, so the token wins — the same call C085 made, recorded there as
    /// gap (b) and still open. 56pt also clears the 44pt minimum tap target with room for
    /// Dynamic Type.
    static let ctaHeight: CGFloat = 56
    static let ctaRadius: CGFloat = MageRideRadius.sm
    static let ctaIcon: CGFloat = 20

    /// The HIG's minimum tap target. Anything interactive is at least this on both axes.
    static let minimumTapTarget: CGFloat = 44

    /// A hairline. `0.5` is the wireframe's `.5px` separator and is a real half-point on 2x/3x.
    static let hairline: CGFloat = 0.5

    /// The leading glyph square on a drawer or grouped-list row (`28 x 28`).
    static let listRowIcon: CGFloat = 28

    /// A glyph sitting inside a row or a chip-sized control.
    static let rowIcon: CGFloat = 20

    /// The recentre control on the map (`.fab`, `42 x 42`), rounded up to the tap-target floor.
    /// D2' §0.3: *"Recentre FAB both apps"*.
    static let mapControl: CGFloat = MageRideControl.minimumTapTarget

    /// SCR-PI-033's identity disc on the Menu tab (`.avatar`, `40 x 40`).
    ///
    /// The card itself is **C101's** — the shell leaves it as a slot with a brand-only default,
    /// which is the call C076 made: a greyed-out *"Your name"* is how a half-built screen ships
    /// looking finished.
    static let avatarSmall: CGFloat = 40

    // MARK: - C095 · the first run
    //
    // `passenger_ios.html`'s own pixels for the cluster-1 cells, rounded onto the 4pt grid where the
    // HTML uses one that is not on it.

    /// The outline on a selected row — SCR-PI-002's `border:1.5px solid var(--primary)`.
    ///
    /// Not on the 4pt grid and not meant to be: it is the wireframe's own hairline-plus, and it is
    /// what makes the chosen language legible without a glyph. The driver wireframe uses a trailing
    /// `✓` instead, which is why this token exists here and not there.
    static let selectedBorder: CGFloat = 1.5

    /// SCR-PI-004's `.avatar.lg` — the profile disc.
    static let avatarLarge: CGFloat = 84

    /// The `＋` camera badge on it (`.cam`).
    static let avatarBadge: CGFloat = 28

    /// SCR-PI-002's and SCR-PI-005's illustration panel (`.illus`).
    static let illustrationPanel: CGFloat = 96

    /// The glyph inside an avatar or an empty illustration panel.
    static let illustrationIcon: CGFloat = 40

    // MARK: - C096 · the live map, the filter and the search
    //
    // `passenger_ios.html`'s own pixels for the cluster-2 cells, rounded onto the 4pt grid — or up to
    // the tap-target floor, where the mock's CSS pixel is below it.

    /// The sheet grabber (`.grabber`, `36 x 5`).
    ///
    /// Drawn rather than left to the system: SCR-PI-010's sheet is **not** a presented `.sheet` (see
    /// ``LiveMapScreen``), so there is no `.presentationDragIndicator` to ask for. SCR-PI-006's and
    /// SCR-PI-007's are, and they take the system's.
    static let grabberWidth: CGFloat = 36
    static let grabberHeight: CGFloat = 5

    /// SCR-PI-010's *"🔍 Where to?"* bar (`.searchbar`, `38` high).
    ///
    /// Raised to the tap-target floor for ``mapControl``'s reason: 38pt is a CSS mock's number and
    /// this one is a button.
    static let searchBar: CGFloat = MageRideControl.minimumTapTarget

    /// SCR-PI-008's `📌 Select on map` / `＋ Add address` pair (`.btn-out`, `46` high).
    ///
    /// Left at the wireframe's 46 rather than rounded to 48: it already clears 44, and the pair sits
    /// directly under the CTA-shaped `.cta` in other cells, where 46 is what distinguishes them.
    static let outlinedAction: CGFloat = 46

    /// The coloured disc on a filter chip (`.vico`, `18 x 18`) — MAP-03's legend colour, so the chip
    /// and the marker it controls are recognisably the same thing.
    static let chipSwatch: CGFloat = 18

    /// A glyph inside a chip, or inside ``chipSwatch``.
    static let chipIcon: CGFloat = 12

    /// The narrowest a SCR-PI-006 type chip may be before the grid wraps.
    ///
    /// The widest of the eight labels (*"Three-wheeler"*, and its Sinhala and Tamil counterparts)
    /// beside a ``chipSwatch``, at the Large content size. An adaptive grid needs a floor, not a
    /// width — a longer label grows the chip rather than truncating it.
    static let filterChipMinimum: CGFloat = 108

    /// SCR-PI-008's `.cdot` — the green pickup / red drop-off dot beside a field.
    static let routeDot: CGFloat = 10

    /// SCR-PI-007's sheet height. The cell's own `Δ iOS` clause is `.sheet(.height(220))`.
    static let vehiclePopupHeight: CGFloat = 220

    // MARK: - C097 · booking, proxy, package and schedule
    //
    // `passenger_ios.html`'s cluster-3 pixels, on the 4pt grid or at the tap-target floor.

    /// The coloured square on a `.tier` row (`.vi`, `36 x 36`) — MAP-03's legend colour again, so a
    /// tier card and the marker for that type are recognisably the same vehicle.
    static let tierIcon: CGFloat = 36

    /// The ring the wireframe draws around a selected `.tier` (`box-shadow:0 0 0 2px`).
    static let selectionRing: CGFloat = 2

    /// SCR-PI-012a's pin preview (`.card > .map`, `90` high).
    static let pinPreview: CGFloat = 90

    /// SCR-PI-012a's own `.sheet` detent. Taller than SCR-PI-007's because it holds a field, a
    /// preview card and two buttons — the cell's frame, measured.
    static let pasteSheetHeight: CGFloat = 460

    /// SCR-PI-009's map, which is a backdrop rather than the work (`flex:0 0 150px`).
    static let bookingMapHeight: CGFloat = 150
}

extension View {

    /// §0.2's iOS elevation, as one modifier so the three numbers are written once.
    func mageElevated() -> some View {
        shadow(
            color: .black.opacity(MageRideElevation.shadowOpacity),
            radius: MageRideElevation.shadowRadius,
            x: 0,
            y: MageRideElevation.shadowOffsetY
        )
    }
}
