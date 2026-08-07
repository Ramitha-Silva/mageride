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

    /// SCR-PA-033's identity disc in the drawer header (`.avatar`, `40 x 40`).
    ///
    /// The header itself is **C102's** — the shell leaves it as a slot with a brand-only default,
    /// which is the call C076 made: a greyed-out *"Your name"* is how a half-built screen ships
    /// looking finished.
    static let avatarSmall: CGFloat = 40
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
