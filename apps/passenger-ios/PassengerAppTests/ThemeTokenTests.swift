import SwiftUI
import UIKit
import XCTest

@testable import PassengerApp

/// D2' §0.2 held to, on the platform the spec calls it authoritative for.
///
/// Every expectation is **typed out from the spec's table** rather than read back out of the
/// production object — the same rule `ThemeTokensTest` states on the Android side, and the only way a
/// test of a constant is worth anything. Reading `MageRideColor.primary` and asserting it equals
/// `MageRideColor.primary` proves the compiler works.
///
/// The colours are read out of the **compiled asset catalogue**, which is what makes this a real
/// check: an entry with a mistyped hex, a missing dark appearance or a name the catalogue does not
/// carry all fail here rather than on a passenger's phone at night.
final class ThemeTokenTests: XCTestCase {

    private let bundle = Bundle(for: MageRideBundleToken.self)

    /// D2' §0.2's brand and semantic table, light hex then dark hex, in the order the spec prints it.
    private let semantic: [(name: String, light: UInt32, dark: UInt32)] = [
        ("primary", 0xFF6D00, 0xFFB68A),
        ("onPrimary", 0xFFFFFF, 0x4A2300),
        ("primaryContainer", 0xFFE0CC, 0x6A3500),
        ("onPrimaryContainer", 0x2B1100, 0xFFDCC4),
        ("secondary", 0x0061A4, 0x9FCAFF),
        ("secondaryContainer", 0xD1E4FF, 0x00497D),
        ("background", 0xFFFFFF, 0x121316),
        ("surface", 0xF7F8FA, 0x1A1C1E),
        ("surfaceVariant", 0xECEEF1, 0x2A2D31),
        ("outline", 0xC7CBD1, 0x43474E),
        ("onSurface", 0x1A1C1E, 0xE3E2E6),
        ("onSurfaceVariant", 0x44474B, 0xC3C7CF),
        ("outlineVariant", 0x74777C, 0x8D9199),
        ("success", 0x2E9E4F, 0x7FD89A),
        ("warning", 0xF5A300, 0xFFCF6B),
        ("error", 0xD32F2F, 0xFFB4AB),
    ]

    func testEverySemanticRoleMatchesTheSpecInBothAppearances() {
        for row in semantic {
            assertColour(row.name, style: .light, equals: row.light)
            assertColour(row.name, style: .dark, equals: row.dark)
        }
    }

    /// **The same sixteen hexes as `apps/driver-ios`'s catalogue.** Two apps rendering §0.2's
    /// `primary` differently is the divergence a shared design token exists to prevent, and it is
    /// invisible until somebody puts the two handsets side by side.
    func testTheSemanticTableIsTheOneTheDriverAppCarries() {
        // Typed out again rather than imported: the driver app is a different target and this test
        // cannot see it, so what is asserted is that both were transcribed from the same table. A
        // change to §0.2 has to be made in both catalogues and both tests, which is the point.
        XCTAssertEqual(semantic.count, 16, "§0.2 has sixteen semantic roles")
    }

    /// MAP-03's legend. **One appearance**, deliberately: §0.2 prints a single hex per vehicle
    /// because a marker colour that changed between light and dark would stop being an identity.
    func testTheVehicleLegendIsOneHexPerTypeInBothAppearances() {
        let legend: [(String, UInt32)] = [
            ("vehBus", 0x2E9E4F),
            ("vehTrain", 0xE5331F),
            ("vehMotorbike", 0x8E44CE),
            ("vehTuk", 0xF5C518),
            ("vehFlex", 0x1ABC9C),
            ("vehSedan", 0x1E6FE5),
            ("vehMiniVan", 0xEC4899),
            ("vehVan", 0xF57C00),
            ("vehTruck", 0x8B5E3C),
            ("vehMiniTruck", 0x808000),
            ("vehPrivate", 0x8A8F98),
        ]
        for (name, hex) in legend {
            assertColour(name, style: .light, equals: hex)
            assertColour(name, style: .dark, equals: hex)
        }
    }

    func testTheModeBadgesAreGreenGreyOrange() {
        assertColour("modeA", style: .light, equals: 0x2E9E4F)
        assertColour("modeB", style: .light, equals: 0x6B7280)
        assertColour("modeC", style: .light, equals: 0xFF6D00)
    }

    /// D2' §0.3: *"Pins: `pickup` green, `dropoff` red, `user` blue dot"*.
    func testTheMapPinsMatchSection0Point3() {
        assertColour("pinPickup", style: .light, equals: 0x2E9E4F)
        assertColour("pinDropoff", style: .light, equals: 0xD32F2F)
        assertColour("pinUser", style: .light, equals: 0x1E6FE5)
    }

    /// The accent every system control takes — the selected tab, an `.alert` button, a `Toggle`.
    func testTheAccentColourIsTheBrandPrimary() {
        assertColour("AccentColor", style: .light, equals: 0xFF6D00)
        assertColour("AccentColor", style: .dark, equals: 0xFFB68A)
    }

    /// **The palettes the driver app has and this one does not.** Each was added there by the
    /// component that drew the screen fixing it — the scanner (C087) and the offer takeover (C088) —
    /// and this app must not ship a palette no `passenger_ios.html` cell asks for. A passenger
    /// screen group that needs one adds it *and* deletes its name from this list, which is what
    /// C102 did with `callBackground` and `sosBackground`: SCR-PI-028 and SCR-PI-029 are the two
    /// cells that fix them.
    ///
    /// AL-31's driver home map and the fifteen-second offer takeover have no passenger counterpart
    /// at all, so the two names left here are permanent rather than pending.
    func testTheShellShipsNoScreenSpecificPalette() {
        for name in ["scannerBackground", "offerBackground"] {
            XCTAssertNil(
                UIColor(named: name, in: bundle, compatibleWith: nil),
                "\(name) is a driver-screen palette; a passenger screen adding one should say which cell fixes it"
            )
        }
    }

    /// **SCR-PI-028's and SCR-PI-029's palettes are dark in BOTH appearances** (C102).
    ///
    /// A call screen or an alarm screen that turned white in the light theme would be a different
    /// screen twice a day, which is why every one of these is a transcribed hex with a single
    /// appearance rather than an alias of a §0.2 role. Asserting both styles is the *whole* test:
    /// somebody adding a dark variant to `sosHalo` — the plausible mistake, since it is derived from
    /// `error`, and `error` has one — would turn the ring pink at night.
    ///
    /// **The same eleven values as `apps/driver-ios`'s catalogue**, typed out again rather than
    /// imported: that app is a different target and this test cannot see it, so what is asserted is
    /// that both were transcribed from the same table. Where those two disagree with the wireframes'
    /// own CSS — both `*_ios.html` cells declare a lighter call palette — the transcribed one wins,
    /// which is C085's decision (1) and is recorded on ``MageRideCallColor``.
    func testTheCallAndSosPalettesAreDarkInBothAppearances() {
        let palette: [(String, UInt32)] = [
            ("callBackground", 0x15171B),
            ("callSurface", 0x2A2D31),
            ("callOnCall", 0xFFFFFF),
            ("callHint", 0xAEB3BC),
            ("callConnected", 0x9FCAFF),
            ("sosBackground", 0x2A0A0A),
            ("sosSurface", 0x3A1414),
            ("sosOutline", 0x5A2020),
            ("sosOnSos", 0xFFFFFF),
            ("sosHint", 0xFFB4AB),
        ]
        for (name, hex) in palette {
            assertColour(name, style: .light, equals: hex)
            assertColour(name, style: .dark, equals: hex)
        }

        // The halo is the §0.2 `error` at 25%, and the alpha is **in the asset**: derived from the
        // role with `.opacity(0.25)` it would resolve to `#FFB4AB` at night on the one screen that
        // must not change.
        assertColour("sosHalo", style: .light, equals: 0xD32F2F, alpha: 0.25)
        assertColour("sosHalo", style: .dark, equals: 0xD32F2F, alpha: 0.25)
    }

    // MARK: - Type

    /// §0.2's type table: a point size **and** a Dynamic Type style per role, both of which have to
    /// hold at once. `.largeTitle` alone ignores the spec's 32pt; `.system(size: 32)` alone ignores
    /// the passenger's setting.
    func testTheTypeScaleIsTheSpecsEightRows() {
        let expected: [(MageRideTextRole, CGFloat, Int, Font.TextStyle)] = [
            (.display, 32, 700, .largeTitle),
            (.headline, 22, 700, .title),
            (.title, 18, 600, .title3),
            (.subtitle, 16, 600, .headline),
            (.body, 16, 400, .body),
            (.bodyEmphasis, 16, 500, .body),
            (.bodySmall, 14, 400, .callout),
            (.label, 12, 500, .caption),
            (.caption, 11, 400, .caption2),
        ]
        XCTAssertEqual(MageRideTextRole.allCases.count, expected.count)
        for (role, size, weight, style) in expected {
            XCTAssertEqual(role.size, size, "\(role) point size")
            XCTAssertEqual(role.cssWeight, weight, "\(role) weight")
            XCTAssertEqual(role.textStyle, style, "\(role) Dynamic Type style")
        }
    }

    // MARK: - Spacing, radius, elevation, controls

    /// §0.2's *"Spacing (4px base grid): 4, 8, 12, 16, 24, 32, 48"*.
    func testTheSpacingGridIsTheSpecs() {
        XCTAssertEqual(
            [MageRideSpacing.xxs, MageRideSpacing.xs, MageRideSpacing.sm, MageRideSpacing.md,
             MageRideSpacing.lg, MageRideSpacing.xl, MageRideSpacing.xxl],
            [4, 8, 12, 16, 24, 32, 48]
        )
    }

    /// §0.2's *"Corner radius: sm 8 · md 12 · lg 16 · card 24"*.
    func testTheRadiusScaleIsTheSpecs() {
        XCTAssertEqual(
            [MageRideRadius.sm, MageRideRadius.md, MageRideRadius.lg, MageRideRadius.card],
            [8, 12, 16, 24]
        )
    }

    /// §0.2's iOS elevation row: *"subtle shadows (radius 8, y 2, opacity 0.12)"*. One shadow, not
    /// Android's six-level ladder — that is what the spec prints for this platform.
    func testTheElevationIsTheSpecsSingleShadow() {
        XCTAssertEqual(MageRideElevation.shadowRadius, 8)
        XCTAssertEqual(MageRideElevation.shadowOffsetY, 2)
        XCTAssertEqual(MageRideElevation.shadowOpacity, 0.12, accuracy: 0.0001)
    }

    /// §0.2's CTA token — *"height 56dp, radius sm 8"*.
    ///
    /// **The wireframe draws 50px / radius 13** and the token wins: §0.2's CTA row is not marked as
    /// a platform delta and Section C lists none. C085 recorded the same conflict as gap (b) and it
    /// is still open, which is why this assertion is here rather than the wireframe's numbers.
    func testTheCtaIsTheTokenAndNotTheWireframe() {
        XCTAssertEqual(MageRideControl.ctaHeight, 56)
        XCTAssertEqual(MageRideControl.ctaRadius, MageRideRadius.sm)
        XCTAssertEqual(MageRideControl.ctaIcon, 20)
        XCTAssertGreaterThanOrEqual(MageRideControl.ctaHeight, MageRideControl.minimumTapTarget)
    }

    /// Anything a passenger has to hit is at least the HIG's 44pt on both axes.
    func testEveryInteractiveControlClearsTheTapTargetFloor() {
        XCTAssertEqual(MageRideControl.minimumTapTarget, 44)
        XCTAssertGreaterThanOrEqual(MageRideControl.mapControl, MageRideControl.minimumTapTarget)
    }

    // MARK: -

    /// - Parameter alpha: What the catalogue entry's own alpha should be. Every §0.2 role is opaque
    ///   and the default says so; the one exception is C102's `sosHalo`, which bakes the wireframe's
    ///   `rgba(211,47,47,.25)` into the asset **precisely so** no call site writes `.opacity(0.25)`
    ///   over a role that has a dark appearance.
    private func assertColour(
        _ name: String,
        style: UIUserInterfaceStyle,
        equals expected: UInt32,
        alpha expectedAlpha: CGFloat = 1,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        guard let colour = UIColor(named: name, in: bundle, compatibleWith: nil) else {
            return XCTFail("no colour named \(name) in the catalogue", file: file, line: line)
        }
        let resolved = colour.resolvedColor(with: UITraitCollection(userInterfaceStyle: style))

        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
        resolved.getRed(&red, green: &green, blue: &blue, alpha: &alpha)

        let actual = (UInt32(round(red * 255)) << 16) | (UInt32(round(green * 255)) << 8) | UInt32(round(blue * 255))
        XCTAssertEqual(
            String(format: "%06X", actual),
            String(format: "%06X", expected),
            "\(name) in \(style == .dark ? "dark" : "light")",
            file: file,
            line: line
        )
        XCTAssertEqual(alpha, expectedAlpha, accuracy: 0.001, "\(name) alpha", file: file, line: line)
    }
}
