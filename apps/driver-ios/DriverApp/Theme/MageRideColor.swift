import SwiftUI

// D2' §0.2 — "AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI", and for
// SwiftUI in particular: "Color asset catalog (same hex, light/dark appearances)".
//
// The hexes therefore live in `Resources/Assets.xcassets` and NOT in this file. That is the whole
// point of the asset catalogue on this platform: the system resolves the appearance, so a colour
// is correct in dark mode, in Increase Contrast and in a `.colorScheme` preview without a single
// branch in Swift. `ThemeTokenTests` reads the catalogue back and compares every component against
// the spec's table, which is the only way a test of a constant is worth anything.
//
// The same sixteen hexes are `apps/driver-android`'s `ui/theme/Color.kt` and the Tailwind preset's
// (AL-52). A value that drifts here makes the three surfaces disagree in a way only a screenshot
// review catches.
//
// **The wireframe's own palette is NOT the source of truth.** `specs/wireframes/driver_ios.html`
// declares slightly different semantic hexes (`--surface:#F2F3F5`, `--error:#FF3B30`, …) because it
// is an HTML mock approximating iOS system colours in a browser. §0.2 is explicit that SwiftUI
// takes the same hex as Compose, so the catalogue is §0.2's and the divergence is recorded in the
// C085 handoff. Where the wireframe uses a genuine HIG system colour — the green `Toggle`, the blue
// `.alert` actions — the app uses SwiftUI's own, because those are the platform's and not ours.

/// The D2' §0.2 palette, by role.
///
/// Reached as `MageRideColor.primary` rather than `Color("primary")` so a typo is a compile error
/// and so the bundle lookup is written once — a colour loaded from `Bundle.main` resolves to
/// nothing inside a SwiftUI preview or a unit-test host.
enum MageRideColor {

    // MARK: - Brand and semantic roles

    static let primary = named("primary")
    static let onPrimary = named("onPrimary")
    static let primaryContainer = named("primaryContainer")
    static let onPrimaryContainer = named("onPrimaryContainer")
    static let secondary = named("secondary")
    static let secondaryContainer = named("secondaryContainer")
    static let background = named("background")
    static let surface = named("surface")
    static let surfaceVariant = named("surfaceVariant")
    static let outline = named("outline")
    static let onSurface = named("onSurface")
    static let onSurfaceVariant = named("onSurfaceVariant")
    static let outlineVariant = named("outlineVariant")

    // MARK: - Status
    //
    // `success` and `warning` are MageRide's, not the platform's, and they carry meaning the app
    // cannot express otherwise: the daily-fee chip is "PAID" in `success` and "DUE" in `warning`,
    // and a verification row is Verified or Pending in the same pair. `onStatus` is the label
    // colour that meets contrast on either.

    static let success = named("success")
    static let warning = named("warning")
    static let error = named("error")
    static let onStatus = named("onStatus")

    // MARK: - Map pins (D2' §0.3)

    static let pinPickup = named("pinPickup")
    static let pinDropoff = named("pinDropoff")
    static let pinUser = named("pinUser")

    /// The bundle the catalogue is in — the app's, resolved off a type rather than `Bundle.main`.
    static let bundle = Bundle(for: MageRideBundleToken.self)

    private static func named(_ name: String) -> Color {
        Color(name, bundle: bundle)
    }
}

/// Anchors `Bundle(for:)` on the app target.
///
/// SwiftUI's `App` and every view here are structs, and `Bundle(for:)` needs a class. `Bundle.main`
/// is deliberately not used: it is the *test host* when a unit test runs, so a resource lookup
/// through it finds nothing and the failure looks like a missing asset rather than a missing bundle.
final class MageRideBundleToken {}

/// MAP-03's vehicle legend and D2' §0.2's mode badges.
///
/// One appearance each, unlike the semantic roles: §0.2 prints a single hex per vehicle because a
/// marker colour that changed between light and dark would stop being an identity. The same eleven
/// hexes are the `--veh*` custom properties in `specs/wireframes/driver_ios.html`.
enum MageRideVehicleColor {
    static let bus = named("vehBus")
    static let train = named("vehTrain")
    static let motorbike = named("vehMotorbike")
    static let threeWheeler = named("vehTuk")
    static let flex = named("vehFlex")
    static let sedan = named("vehSedan")
    static let miniVan = named("vehMiniVan")
    static let van = named("vehVan")
    static let truck = named("vehTruck")
    static let miniTruck = named("vehMiniTruck")
    static let privateHire = named("vehPrivate")

    /// Mode A green, Mode B grey, Mode C orange.
    static let modeA = named("modeA")
    static let modeB = named("modeB")
    static let modeC = named("modeC")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}

/// SCR-DI-005's viewfinder palette (C087).
///
/// **The one screen in this app that is not on the semantic scheme**, and the wireframe is explicit
/// about it: `driver_ios.html` draws the capture cell on `#0f1115` with a `#FFB68A` accent and a grey
/// hint, and a scanner that turned white in daylight would be a different screen twice a day. A
/// viewfinder has one appearance, exactly as the vehicle legend does.
///
/// Still colour *assets* and not hexes in Swift, which is this target's rule without an exception:
/// the catalogue is where a colour is declared, and `ThemeTokenTests` is what reads one back. The
/// same four values are `apps/driver-android/.../ui/theme/Color.kt`'s `ScannerColors`, plus the white
/// that file spells inline.
enum MageRideScannerColor {

    /// The screen the viewfinder sits on.
    static let background = named("scannerBackground")

    /// The crop quad, the flash and `Use photo ›`.
    static let accent = named("scannerAccent")

    /// The hint line and any control that is not yet live.
    static let hint = named("scannerHint")

    /// Titles and `Retake`, at full contrast on the dark screen.
    static let onScanner = named("scannerOnScanner")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}

/// SCR-DI-014's takeover palette (C088).
///
/// **The second screen in this app that is not on the semantic scheme**, after the scanner, and the
/// wireframe is as explicit about it: `driver_ios.html` draws the dispatch cell on `#15171B` with
/// `#1f2227` cards, `#AEB3BC` captions, a `#444` outline on **Reject** and the `#FFB68A` fee note.
/// A fifteen-second offer that turned white in daylight would be a different screen twice a day, and
/// unlike every other screen it has to be read through a windscreen in one glance.
///
/// One appearance each, exactly as the vehicle legend has: the takeover is dark in both. Still colour
/// *assets* and not hexes in Swift, which is this target's rule without an exception — the catalogue
/// is where a colour is declared and `ThemeTokenTests` is what reads one back.
enum MageRideOfferColor {

    /// The screen the takeover sits on (`#15171B`).
    static let background = named("offerBackground")

    /// The `● Pickup` / `◆ Drop` card on it (`#1f2227`).
    static let surface = named("offerSurface")

    /// The fare, the badges' labels and the countdown, at full contrast.
    static let onOffer = named("offerOnOffer")

    /// A caption on the dark screen — the two place labels (`#AEB3BC`).
    static let muted = named("offerMuted")

    /// **Reject**'s hairline (`#444`). Deliberately not `outline`: on this background §0.2's
    /// `outline` is a light grey and would read as the primary action.
    static let outline = named("offerOutline")

    /// US-9.1's *"2nd trip — Rs 100 daily fee deducts on accept"* line (`#FFB68A`).
    static let accent = named("offerAccent")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}

/// SCR-DI-031's palette (C093).
///
/// The **third** screen in this app that is not on the semantic scheme, after the scanner and the
/// offer takeover, and the wireframe is as explicit about it: `driver_ios.html` draws the call cell
/// on a `#3a3d44 → #15171B` gradient with a `#4a4d55` avatar disc and `#cfd3da` captions. A call
/// screen that turned white in the light theme would be a different screen twice a day — the same
/// argument ``MageRideScannerColor`` makes, and one that matters more here: the driver is looking at
/// it while driving.
///
/// One appearance each, exactly as the vehicle legend and the offer takeover have. Still colour
/// *assets* and not hexes in Swift, which is this target's rule without an exception. The same five
/// values are `apps/driver-android/.../ui/theme/Color.kt`'s `CallColors`.
enum MageRideCallColor {

    /// The screen the call sits on (`#15171B` — the gradient's foot, which is what the flat
    /// SwiftUI background takes; the wireframe's gradient is CSS chrome, not a §0.2 token).
    static let background = named("callBackground")

    /// The avatar disc and the two inert `.fab` controls (`#2A2D31`).
    static let surface = named("callSurface")

    /// The callee's name and an active toggle's label, at full contrast.
    static let onCall = named("callOnCall")

    /// *"In-app call · number hidden"* and the avatar's glyph (`#AEB3BC`).
    static let hint = named("callHint")

    /// *"Connected · 00:42"* — §0.2's `secondary` on a dark screen (`#9FCAFF`), which is the dark
    /// appearance of that role rather than a sixth colour.
    static let connected = named("callConnected")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}

/// SCR-DI-032's palette (C093).
///
/// The wireframe draws the driver SOS on `#2A0A0A` with a `#3A1414` contact card, a `#FFB4AB` status
/// line and the §0.2 `error` disc inside a translucent halo of itself. **Dark in both appearances**,
/// like ``MageRideCallColor`` and ``MageRideScannerColor``: an alarm screen that could be mistaken
/// for an ordinary one is the failure mode this palette exists to prevent.
///
/// The same six values are `apps/driver-android/.../ui/theme/Color.kt`'s `SosColors`.
enum MageRideSosColor {

    /// The screen (`#2A0A0A`).
    static let background = named("sosBackground")

    /// The emergency-contact card (`#3A1414`).
    static let surface = named("sosSurface")

    /// That card's border (`#5A2020`). Deliberately not §0.2's `outline`, which is a light grey on
    /// this background and would read as a control.
    static let outline = named("sosOutline")

    /// The title and the card's text.
    static let onSos = named("sosOnSos")

    /// *"Sending GPS + active trip via SMS…"* (`#FFB4AB`).
    static let hint = named("sosHint")

    /// The ring around the disc — the §0.2 `error` at 25% (`rgba(211,47,47,.25)`), which is the
    /// wireframe's own `box-shadow: 0 0 0 14px`.
    ///
    /// A catalogue entry with its alpha baked in rather than `MageRideColor.error.opacity(0.25)`,
    /// because `error` has a **dark appearance** (`#FFB4AB`) and this screen is dark in both — a halo
    /// derived from the role would turn pink at night on the one screen that must not change.
    static let halo = named("sosHalo")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}
