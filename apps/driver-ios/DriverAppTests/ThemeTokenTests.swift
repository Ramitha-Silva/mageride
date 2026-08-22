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

    /// SCR-DI-014's takeover palette (Δ C088).
    ///
    /// **Not §0.2's, and that is the point.** `driver_ios.html` draws the dispatch cell on a fixed dark
    /// chrome, exactly as it draws the scanner on one, and a fifteen-second offer that turned white in
    /// daylight would be a different screen twice a day. One appearance each, like the vehicle legend:
    /// asserted in **both** so a dark variant added later fails here rather than at a junction.
    func testTheOfferTakeoverPaletteIsTheWireframesAndHasOneAppearance() {
        let takeover: [(String, UInt32)] = [
            ("offerBackground", 0x15171B),
            ("offerSurface", 0x1F2227),
            ("offerOnOffer", 0xFFFFFF),
            ("offerMuted", 0xAEB3BC),
            ("offerOutline", 0x444444),
            ("offerAccent", 0xFFB68A),
        ]
        for (name, hex) in takeover {
            assertColour(name, style: .light, equals: hex)
            assertColour(name, style: .dark, equals: hex)
        }
    }

    /// SCR-DI-031's and SCR-DI-032's palettes (Δ C093).
    ///
    /// The fourth and fifth things in this app that are not on §0.2's scheme, after the scanner, the
    /// offer takeover and the vehicle legend — and the wireframe is explicit about both: the call
    /// cell is drawn on a `#3a3d44 → #15171B` gradient and the alarm on `#2A0A0A`. One appearance
    /// each, asserted in **both** so a dark variant added later fails here rather than at a junction.
    ///
    /// The same ten values are `apps/driver-android/.../ui/theme/Color.kt`'s `CallColors` and
    /// `SosColors`. `sosHalo` is the eleventh and is asserted separately, because its alpha is the
    /// whole point of it.
    func testTheCallAndAlarmPalettesAreTheWireframesAndHaveOneAppearance() {
        let fixed: [(String, UInt32)] = [
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
        for (name, hex) in fixed {
            assertColour(name, style: .light, equals: hex)
            assertColour(name, style: .dark, equals: hex)
        }
    }

    /// The halo is §0.2's `error` at **25%** — the wireframe's `box-shadow: 0 0 0 14px
    /// rgba(211,47,47,.22)`, rounded to the quarter the Android twin uses.
    ///
    /// A catalogue entry with its alpha baked in rather than `MageRideColor.error.opacity(0.25)`,
    /// because `error` has a dark appearance (`#FFB4AB`) and this screen is dark in both: a halo
    /// derived from the role would turn pink at night on the one screen that must not change.
    func testTheAlarmHaloIsTheErrorRoleAtAQuarterAndDoesNotFollowTheAppearance() throws {
        for style in [UIUserInterfaceStyle.light, .dark] {
            let colour = try XCTUnwrap(
                UIColor(named: "sosHalo", in: bundle, compatibleWith: UITraitCollection(userInterfaceStyle: style))
            )
            var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, alpha: CGFloat = 0
            XCTAssertTrue(colour.getRed(&red, green: &green, blue: &blue, alpha: &alpha))

            XCTAssertEqual(UInt32(round(red * 255)), 0xD3)
            XCTAssertEqual(UInt32(round(green * 255)), 0x2F)
            XCTAssertEqual(UInt32(round(blue * 255)), 0x2F)
            XCTAssertEqual(alpha, 0.25, accuracy: 0.005)
        }
    }

    /// C093's control sizes — the wireframe's own pixels, rounded onto the 4pt grid and up to the
    /// 44pt tap-target floor where the control is interactive.
    func testTheCallAndAlarmControlSizesAreTheWireframesRoundedOntoTheGrid() {
        XCTAssertEqual(MageRideControl.callAction, 44, "the wireframe's 42pt `.fab`, at the HIG floor")
        XCTAssertEqual(MageRideControl.callEnd, 64, "its 62pt hang-up disc, on the grid")
        XCTAssertEqual(MageRideControl.avatarLarge, 84, "`.avatar.lg`")
        XCTAssertEqual(MageRideControl.sosButton, 128)
        XCTAssertEqual(MageRideControl.sosHalo, 16)
        XCTAssertEqual(MageRideControl.searchBar, 44, "the wireframe's 38pt `.searchbar`, at the HIG floor")

        for size in [MageRideControl.callAction, MageRideControl.callEnd, MageRideControl.searchBar] {
            XCTAssertGreaterThanOrEqual(size, MageRideControl.minimumTapTarget)
        }
        XCTAssertGreaterThan(
            MageRideControl.sosButton,
            MageRideControl.bigToggle,
            "the alarm is the largest control in the app — it is pressed by somebody not looking"
        )
    }

    /// C088's control sizes — the same numbers as `apps/driver-android/.../ui/theme/Dimens.kt`, because
    /// the two apps draw the same wireframe cell at the same size.
    func testTheDashboardControlSizesMatchTheAndroidTokens() {
        XCTAssertEqual(MageRideControl.bigToggle, 64)
        XCTAssertEqual(MageRideControl.countdownRing, 96)
        XCTAssertEqual(MageRideControl.countdownStroke, 8)
        XCTAssertEqual(MageRideControl.mapPreview, 120)
        XCTAssertEqual(MageRideControl.avatarSmall, 40)
        XCTAssertEqual(MageRideControl.rowIcon, 20)
        XCTAssertGreaterThanOrEqual(
            MageRideControl.bigToggle,
            MageRideControl.minimumTapTarget,
            "the most-tapped control in the app clears the HIG floor"
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
        guard let dynamic = UIColor(named: name, in: bundle, compatibleWith: nil) else {
            XCTFail("no colour set named '\(name)' in the asset catalogue", file: file, line: line)
            return
        }
        // `resolvedColor(with:)` rather than `compatibleWith:`. An asset-catalogue colour is a
        // DYNAMIC colour: `compatibleWith:` hands back that dynamic value unresolved, which then
        // reads against the process's own appearance — so both arms of every row were measuring the
        // light hex and every light/dark pair silently agreed. This is the call that resolves it.
        let colour = dynamic.resolvedColor(with: UITraitCollection(userInterfaceStyle: style))

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
