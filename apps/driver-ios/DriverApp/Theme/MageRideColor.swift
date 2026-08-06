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
