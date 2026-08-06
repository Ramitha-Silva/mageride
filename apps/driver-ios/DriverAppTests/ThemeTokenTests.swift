import SwiftUI
import UIKit
import XCTest

@testable import DriverApp

/// D2' §0.2 held to, on the platform the spec calls it authoritative for.
///
/// Every expectation is **typed out from the spec's table** rather than read back out of the
/// production object — the same rule `ThemeTokensTest` states on the Android side, and the only way
/// a test of a constant is worth anything. Reading `MageRideColor.primary` and asserting it equals
/// `MageRideColor.primary` proves the compiler works.
///
/// The colours are read out of the **compiled asset catalogue**, which is what makes this a real
/// check: an entry with a mistyped hex, a missing dark appearance or a name the catalogue does not
/// carry all fail here rather than on a driver's phone at night.
final class ThemeTokenTests: XCTestCase {

    private let bundle = Bundle(for: MageRideBundleToken.self)

    // MARK: - Colour

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

    /// D2' §0.3: "Pins: `pickup` green, `dropoff` red, `user` blue dot".
    func testTheMapPinsMatchSection0Point3() {
        assertColour("pinPickup", style: .light, equals: 0x2E9E4F)
        assertColour("pinDropoff", style: .light, equals: 0xD32F2F)
        assertColour("pinUser", style: .light, equals: 0x1E6FE5)
    }

    /// The accent every system control takes — a `Toggle`, an `.alert` button, the selected tab.
    func testTheAccentColourIsTheBrandPrimary() {
        assertColour("AccentColor", style: .light, equals: 0xFF6D00)
        assertColour("AccentColor", style: .dark, equals: 0xFFB68A)
    }

    // MARK: - Type

    /// D2' §0.2's typography table: role → iOS Dynamic Type style, point size, weight.
    func testTheTypeScaleIsTheSpecsEightRows() {
        let expected: [(MageRideTextRole, Font.TextStyle, CGFloat, Int)] = [
            (.display, .largeTitle, 32, 700),
            (.headline, .title, 22, 700),
            (.title, .title3, 18, 600),
            (.subtitle, .headline, 16, 600),
            (.body, .body, 16, 400),
            (.bodySmall, .callout, 14, 400),
            (.label, .caption, 12, 500),
            (.caption, .caption2, 11, 400),
        ]

        for (role, style, size, weight) in expected {
            XCTAssertEqual(role.textStyle, style, "\(role) is mapped to the wrong Dynamic Type style")
            XCTAssertEqual(role.size, size, "\(role) is the wrong point size")
            XCTAssertEqual(role.cssWeight, weight, "\(role) is the wrong weight")
        }
    }

    /// The table's "400/500" for Body is one role with an emphasis variant, not two tokens — so the
    /// emphasis case rides the same text style and size and differs only in weight.
    func testBodyEmphasisIsTheSameRowAtWeight500() {
        XCTAssertEqual(MageRideTextRole.bodyEmphasis.textStyle, MageRideTextRole.body.textStyle)
        XCTAssertEqual(MageRideTextRole.bodyEmphasis.size, MageRideTextRole.body.size)
        XCTAssertEqual(MageRideTextRole.bodyEmphasis.cssWeight, 500)
    }

    // MARK: - Spacing, radius, CTA

    func testTheSpacingScaleIsTheFourPointGrid() {
        XCTAssertEqual(
            [
                MageRideSpacing.xxs, MageRideSpacing.xs, MageRideSpacing.sm, MageRideSpacing.md,
                MageRideSpacing.lg, MageRideSpacing.xl, MageRideSpacing.xxl,
            ],
            [4, 8, 12, 16, 24, 32, 48],
            "§0.2: 'Spacing (4px base grid): 4, 8, 12, 16, 24, 32, 48'"
        )
    }

    func testTheCornerRadiiAreTheSpecsFour() {
        XCTAssertEqual(MageRideRadius.sm, 8)
        XCTAssertEqual(MageRideRadius.md, 12)
        XCTAssertEqual(MageRideRadius.lg, 16)
        XCTAssertEqual(MageRideRadius.card, 24)
    }

    /// §0.2's iOS elevation row: "subtle shadows (`radius 8, y 2, opacity 0.12`) + material blur".
    /// One shadow, not Android's six-level ladder — that is what the spec prints for this platform.
    func testTheElevationIsTheSpecsSingleShadow() {
        XCTAssertEqual(MageRideElevation.shadowRadius, 8)
        XCTAssertEqual(MageRideElevation.shadowOffsetY, 2)
        XCTAssertEqual(MageRideElevation.shadowOpacity, 0.12, accuracy: 0.0001)
    }

    /// §0.2's CTA token: "height `56dp`, radius `sm 8` … optional 20dp leading/trailing icon".
    ///
    /// The wireframe draws `height:50px; border-radius:13px`, which is the HIG's button metrics
    /// rather than the token's; §0.2's CTA row carries no platform delta and Section C lists none,
    /// so the token wins. Recorded in the C085 handoff.
    func testTheCtaTokenIsTheSpecsAndNotTheWireframes() {
        XCTAssertEqual(MageRideControl.ctaHeight, 56)
        XCTAssertEqual(MageRideControl.ctaRadius, 8)
        XCTAssertEqual(MageRideControl.ctaIcon, 20)
        XCTAssertGreaterThanOrEqual(
            MageRideControl.ctaHeight,
            MageRideControl.minimumTapTarget,
            "a CTA below the 44pt HIG tap target is unreachable with Dynamic Type turned up"
        )
    }

    // MARK: -

    private func assertColour(
        _ name: String,
        style: UIUserInterfaceStyle,
        equals hex: UInt32,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        guard let colour = UIColor(named: name, in: bundle, compatibleWith: UITraitCollection(userInterfaceStyle: style)) else {
            XCTFail("no colour set named '\(name)' in the asset catalogue", file: file, line: line)
            return
        }

        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
        XCTAssertTrue(colour.getRed(&red, green: &green, blue: &blue, alpha: &alpha), file: file, line: line)

        let actual = (UInt32(round(red * 255)) << 16) | (UInt32(round(green * 255)) << 8) | UInt32(round(blue * 255))
        XCTAssertEqual(
            String(format: "%06X", actual),
            String(format: "%06X", hex),
            "\(name) in \(style == .dark ? "dark" : "light")",
            file: file,
            line: line
        )
        XCTAssertEqual(alpha, 1, accuracy: 0.001, "\(name) is not opaque", file: file, line: line)
    }
}
