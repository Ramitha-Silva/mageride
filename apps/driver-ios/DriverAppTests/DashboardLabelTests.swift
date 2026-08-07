import MageRideShared
import UIKit
import XCTest

@testable import DriverApp

/// The tables cluster 3 renders from — the money formatter, the package labels, the eight Menu rows
/// and the two codes that are data rather than copy.
final class DashboardLabelTests: XCTestCase {

    private let bundle = Bundle(for: MageRideBundleToken.self)

    // MARK: - Money, distance and the two clocks

    /// `Rs 480`, `Rs 480.50`, `Rs 1,240` — whole rupees lose their `.00`, and the grouping does not
    /// follow the handset's region.
    func testRupeesAreGroupedAndLoseAnEmptyCentPair() {
        XCTAssertEqual(MoneyFormat.rupees(48_000), "Rs 480")
        XCTAssertEqual(MoneyFormat.rupees(48_050), "Rs 480.50")
        XCTAssertEqual(MoneyFormat.rupees(48_005), "Rs 480.05")
        XCTAssertEqual(MoneyFormat.rupees(124_000), "Rs 1,240")
        XCTAssertEqual(MoneyFormat.rupees(318_000), "Rs 3,180")
        XCTAssertEqual(MoneyFormat.rupees(0), "Rs 0")
        XCTAssertEqual(MoneyFormat.rupees(-5_000), "Rs -50")
    }

    /// `Rs`, the em dash and the route arrow are proper nouns and symbols, not copy — three identical
    /// values in the three `Localizable.strings` files is exactly what `LocalizationTests` fails on.
    func testTheSymbolsAreConstantsAndNotStringsEntries() {
        XCTAssertEqual(MoneyFormat.prefix, "Rs")
        XCTAssertEqual(MoneyFormat.empty, MageRideSymbols.unknown)
        XCTAssertEqual(MageRideSymbols.unknown, "—")
        XCTAssertEqual(MageRideSymbols.routeArrow, " → ")
        XCTAssertEqual(MageRideSymbols.separator, " · ")

        let english = table(in: "en")
        for absent in ["currency_prefix", "symbol_unknown", "route_arrow"] {
            XCTAssertNil(english?[absent], "\(absent) is a symbol; it must not be a translated string")
        }
    }

    func testDistanceCrossesFromMetresToKilometres() {
        XCTAssertEqual(MoneyFormat.distance(metres: 240), "240 m")
        XCTAssertEqual(MoneyFormat.distance(metres: 999), "999 m")
        XCTAssertEqual(MoneyFormat.distance(metres: 1_000), "1.0 km")
        XCTAssertEqual(MoneyFormat.distance(metres: 1_240), "1.2 km")
        XCTAssertEqual(MoneyFormat.distance(metres: 18_400), "18.4 km")
        XCTAssertEqual(MoneyFormat.distance(metres: -5), "0 m", "a negative distance is not a distance")
    }

    /// SCR-DI-011's `01:12:40` and SCR-DI-013's `1:42` are **different shapes** on purpose: one is a
    /// session that started at a wall-clock time, the other is what is left of an activation.
    func testTheTwoClocksAreDistinct() {
        XCTAssertEqual(MoneyFormat.clock(seconds: 4_360), "01:12:40")
        XCTAssertEqual(MoneyFormat.clock(seconds: 0), "00:00:00")
        XCTAssertEqual(MoneyFormat.clock(seconds: -30), "00:00:00")

        XCTAssertEqual(MoneyFormat.countdown(seconds: 6_120), "1:42")
        XCTAssertEqual(MoneyFormat.countdown(seconds: 59), "0:00")
        XCTAssertEqual(MoneyFormat.countdown(seconds: -1), "0:00")
    }

    /// The Driver Level badge is an identifier, not a sentence.
    func testTheLevelBadgeIsAConstantAndNotCopy() {
        XCTAssertEqual(DashboardLabels.level(3), "L3")
        XCTAssertEqual(DashboardLabels.level(1), "L1")
    }

    // MARK: - Package and payment

    func testEveryPackageSizeAndPaymentMethodResolvesInAllThreeLocales() {
        var keys = [PackageSize.s, PackageSize.m, PackageSize.l].map(PackageLabels.size)
        keys += [
            RidePaymentMethod.cash,
            RidePaymentMethod.lankaqr,
            RidePaymentMethod.onepay,
            RidePaymentMethod.cod,
        ].map { PackageLabels.payment($0) }
        keys.append(PackageLabels.payment(nil))

        XCTAssertEqual(Set(keys).count, 7, "four payment methods and three sizes, no duplicates")
        assertResolvesEverywhere(keys)
    }

    /// A ride with no digital instrument attached is a cash ride — the offer badge is drawn before the
    /// enrichment read lands, so the default has to be the one D5' treats as the default.
    func testAnUnknownPaymentMethodReadsAsCash() {
        XCTAssertEqual(PackageLabels.payment(nil), PackageLabels.payment(RidePaymentMethod.cash))
    }

    // MARK: - SCR-DI-036

    /// **Eight rows, and the same eight as Android.** C090 added three routes to the shell whose entry
    /// points are SCR-DI-010's badge and its earnings line; none of them belongs here.
    func testTheMenuIsTheWireframesEightRowsInOrder() {
        XCTAssertEqual(
            MenuDestination.allCases.map(\.route),
            [.vehicles, .vehicleOnboarding, .trackerPairing, .sharing, .profile, .rideHistory, .support, .notifications]
        )
        XCTAssertEqual(MenuDestination.allCases.count, 8)
    }

    func testEveryMenuRowIsDistinctAndReachable() {
        XCTAssertEqual(Set(MenuDestination.allCases.map(\.labelKey)).count, 8)
        XCTAssertEqual(Set(MenuDestination.allCases.map(\.symbolName)).count, 8)
        for destination in MenuDestination.allCases {
            XCTAssertTrue(
                DriverRoute.staticRoutes.contains(destination.route),
                "\(destination.rawValue) points at a route the shell does not carry"
            )
            XCTAssertNotNil(UIImage(systemName: destination.symbolName), "\(destination.symbolName) does not exist")
        }
    }

    /// **AL-31** — every Menu row lands on the Menu tab, so the system's back button says `‹ Menu`.
    func testEveryMenuRowBelongsToTheMenuTab() {
        for destination in MenuDestination.allCases {
            XCTAssertEqual(destination.route.tab, .menu, "\(destination.rawValue) would switch tabs")
        }
    }

    func testEveryMenuLabelResolvesInAllThreeLocales() {
        assertResolvesEverywhere(MenuDestination.allCases.map(\.labelKey) + ["menu_driver"])
    }

    // MARK: -

    private func assertResolvesEverywhere(_ keys: [String], file: StaticString = #filePath, line: UInt = #line) {
        for locale in ["en", "si", "ta"] {
            guard let table = table(in: locale) else {
                XCTFail("cannot read \(locale).lproj/Localizable.strings", file: file, line: line)
                return
            }
            for key in keys {
                XCTAssertNotNil(table[key], "\(locale) has no \(key)", file: file, line: line)
            }
        }
    }

    private func table(in locale: String) -> [String: String]? {
        guard
            let path = bundle.path(forResource: locale, ofType: "lproj"),
            let localised = Bundle(path: path),
            let url = localised.url(forResource: "Localizable", withExtension: "strings")
        else { return nil }
        return NSDictionary(contentsOf: url) as? [String: String]
    }
}
