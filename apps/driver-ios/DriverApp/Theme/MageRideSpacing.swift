import SwiftUI

// D2' §0.2 — spacing, corner radius, elevation and the CTA token, transcribed for SwiftUI.
//
// The numbers are the same as `apps/driver-android/.../ui/theme/Dimens.kt`, because they are the
// same design contract; what differs is the elevation row, and only because the spec says so:
// "Android = M3 levels 0/1/3/6/8/12dp (surfaceColorAtElevation); **iOS = subtle shadows
// (radius 8, y 2, opacity 0.12) + material blur**". Android tints a surface, iOS casts a shadow —
// the same intent expressed in each platform's own vocabulary, which is Section C's whole subject.
//
// A view uses the token, never a raw number: the 4pt grid is what keeps eight screen groups
// written by eight sessions looking like one app. `ThemeTokenTests` holds the table.

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
/// values here are `specs/wireframes/driver_ios.html`'s, rounded onto the grid where the HTML uses
/// a CSS pixel that is not on it. Anything a later group needs at a new size belongs here too.
enum MageRideControl {

    /// §0.2's CTA token — "height `56dp`, radius `sm 8`, `primary` bg, `onPrimary` label
    /// `titleMedium`, optional 20dp leading/trailing icon".
    ///
    /// **The wireframe draws it at `height:50px; border-radius:13px`**, which is the HIG's own
    /// button metrics rather than the token's. §0.2's CTA row is not marked as a platform delta and
    /// Section C does not list one, so the token wins and the wireframe's numbers are recorded in
    /// the C085 handoff. 56pt also clears the 44pt minimum tap target with room for Dynamic Type.
    static let ctaHeight: CGFloat = 56
    static let ctaRadius: CGFloat = MageRideRadius.sm
    static let ctaIcon: CGFloat = 20

    /// The HIG's minimum tap target. Anything interactive is at least this on both axes.
    static let minimumTapTarget: CGFloat = 44

    /// The sheet grabber (`.grabber`, `36 x 5`). SwiftUI draws its own on a `.sheet`; this is for
    /// the wireframe's inline sheets, which are not presented modally.
    static let grabberWidth: CGFloat = 36
    static let grabberHeight: CGFloat = 4

    /// A hairline. `0.5` is the wireframe's `.5px` separator and is a real half-point on 2x/3x.
    static let hairline: CGFloat = 0.5

    /// The leading glyph square on a grouped-list row (`.glist .gr .ic`, `28 x 28`).
    static let listRowIcon: CGFloat = 28

    /// The recentre control on the map (`.fab`, `42 x 42`), rounded up to the tap-target floor.
    static let mapControl: CGFloat = MageRideControl.minimumTapTarget

    // MARK: - C087 · the Mode-C wizard, the scanner and My Vehicles
    //
    // The same numbers as `apps/driver-android/.../ui/theme/Dimens.kt`'s `ControlTokens`, because
    // the two apps draw the same wireframe cell at the same size.

    /// The `📷 Tap to capture` panel on SCR-DI-004a/b/c (`.illus`, `height:120px`).
    static let capturePanel: CGFloat = 120

    /// The ⏳ / ✓ disc on SCR-DI-006's header (`60 x 60`).
    static let statusAvatar: CGFloat = 60

    /// The coloured vehicle-type dot on a My Vehicles row (`.cdot`).
    static let statusDot: CGFloat = 12

    /// The glyph inside ``statusAvatar`` and ``IllustrationPanel``.
    static let illustrationIcon: CGFloat = 40

    /// A glyph sitting inside a chip or a text button.
    static let chipIcon: CGFloat = 14

    /// SCR-DI-005's shutter (`.shutter`, `64 x 64`).
    static let shutter: CGFloat = 64

    // MARK: - C088 · the dashboard, the offer and the ride
    //
    // The same numbers as `apps/driver-android/.../ui/theme/Dimens.kt`'s `ControlTokens`, because
    // the two apps draw the same wireframe cell at the same size.

    /// SCR-DI-010's `◉ ONLINE — Mode C` bar (`.bigtoggle`). The most-tapped control in the app.
    static let bigToggle: CGFloat = 64

    /// SCR-DI-014's fifteen-second ring (`.ring`) and its stroke.
    static let countdownRing: CGFloat = 96
    static let countdownStroke: CGFloat = 8

    /// SCR-DI-013's map preview (`.map` at `flex:0 0 110px`, rounded onto the 4pt grid).
    static let mapPreview: CGFloat = 120

    /// The 👤 disc on SCR-DI-015's and SCR-DI-036's rows (`.avatar`).
    static let avatarSmall: CGFloat = 40

    /// A glyph sitting inside a row or a chip-sized control.
    static let rowIcon: CGFloat = 20
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
