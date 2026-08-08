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
// The same hexes are `apps/passenger-android`'s `ui/theme/Color.kt`, `apps/driver-ios`'s catalogue
// and the Tailwind preset's (AL-52). A value that drifts here makes four surfaces disagree in a way
// only a screenshot review catches.
//
// **The wireframe's own palette is NOT the source of truth.** `specs/wireframes/passenger_ios.html`
// declares slightly different semantic hexes because it is an HTML mock approximating iOS system
// colours in a browser. §0.2 is explicit that SwiftUI takes the same hex as Compose, so the
// catalogue is §0.2's; C085 recorded the same divergence for the driver wireframe as gap (b) and it
// is still open. Where the wireframe uses a genuine HIG system colour for a genuine system control —
// the blue `.alert` actions — the app uses SwiftUI's own, because those are the platform's.
//
// **This file carries §0.2's roles and nothing else.** The driver app has grown four screen-specific
// palettes (the scanner, the offer takeover, the call, the SOS), each added by the component that
// drew that screen. A passenger screen group that needs one — SCR-PA-029's alarm is the obvious
// candidate — adds it here beside these, with the wireframe cell that fixes it quoted, exactly as
// C087/C088/C093 did on the other side.

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
    // cannot express otherwise: a package's four-step progress bar, a subscription that is active
    // or lapsed, the offline banner. `onStatus` is the label colour that meets contrast on any of
    // the three.

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
/// marker colour that changed between light and dark would stop being an identity. This app draws
/// **every mode in a cell at once**, which is what makes the whole eleven-colour table load-bearing
/// here in a way it is not on the driver's own-vehicle map — see ``VehicleLayers``.
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

/// SCR-PI-028's palette (C102).
///
/// **Dark in both appearances**, which is why every value here is a transcribed hex with one
/// appearance rather than an alias of a semantic role. A call screen that turned white in the light
/// theme would be a different screen twice a day, and a phone call has one appearance — the same
/// argument `apps/driver-ios`'s ``MageRideCallColor`` makes for SCR-DI-031, and the reason C084's
/// handoff asked this side to mirror the values rather than derive them.
///
/// **The five hexes are `apps/driver-ios`'s and `apps/passenger-android`'s `CallColors`, not this
/// wireframe's.** `passenger_ios.html`'s SCR-PI-028 cell declares its own call palette — a
/// `#3a3d44 → #15171B` gradient, a `#4a4d55` avatar and `#cfd3da` captions — and so does
/// `driver_ios.html`'s SCR-DI-031, identically. C085's decision (1) settled that conflict for this
/// platform: **where a wireframe's CSS and the transcribed palette disagree, the palette wins**, so
/// the two iOS apps and the two Android apps all draw one call screen rather than four. The
/// gradient is CSS chrome; the flat background is its foot, which is the value both catalogues carry.
enum MageRideCallColor {

    /// The screen the call sits on (`#15171B`).
    static let background = named("callBackground")

    /// The avatar disc and the two inert `.fab` controls (`#2A2D31`).
    static let surface = named("callSurface")

    /// The callee's name and an active toggle's label, at full contrast.
    static let onCall = named("callOnCall")

    /// *"In-app call · number hidden"*, the avatar's glyph and the `mute` / `speaker` captions the
    /// cell draws under its two `.fab`s (`#AEB3BC`).
    static let hint = named("callHint")

    /// *"Connected · 01:24"* — §0.2's `secondary` on a dark screen (`#9FCAFF`), which is the dark
    /// appearance of that role rather than a sixth colour.
    static let connected = named("callConnected")

    private static func named(_ name: String) -> Color {
        Color(name, bundle: MageRideColor.bundle)
    }
}

/// SCR-PI-029's palette (C102).
///
/// The wireframe draws the passenger SOS on `#2A0A0A` with a `#3A1414` contact card, a `#FFB4AB`
/// status line and the §0.2 `error` disc inside a translucent halo of itself. **Dark in both
/// appearances**, like ``MageRideCallColor``: an alarm screen that could be mistaken for an ordinary
/// one is the failure mode this palette exists to prevent.
///
/// The same six values are `apps/driver-ios`'s ``MageRideSosColor`` and
/// `apps/passenger-android/.../ui/theme/Color.kt`'s `SosColors`.
enum MageRideSosColor {

    /// The screen (`#2A0A0A`).
    static let background = named("sosBackground")

    /// The emergency-contact card (`#3A1414`).
    static let surface = named("sosSurface")

    /// That card's border (`#5A2020`). Deliberately not §0.2's `outline`, which is a light grey on
    /// this background and would read as a control. `passenger_ios.html`'s own cell leaves the card
    /// border at the sheet default; `passenger_android.html`'s names this hex, and the Android app
    /// carries it — so the border is the twin's rather than a sixth value invented here.
    static let outline = named("sosOutline")

    /// The title and the card's text.
    static let onSos = named("sosOnSos")

    /// *"Sending GPS + trip to emergency contacts via SMS…"* (`#FFB4AB`).
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
