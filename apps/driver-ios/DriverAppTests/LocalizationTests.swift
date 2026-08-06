import XCTest

@testable import DriverApp

/// CLAUDE.md's trilingual rule, enforced.
///
/// > *"All user-facing strings must support Si (Sinhala), Ta (Tamil), En (English). Use resource
/// > files, never hardcode strings."*
///
/// The three `Localizable.strings` files are read out of the **built bundle** and compared. What can
/// actually go wrong is a key added to one file and forgotten in the other two, a translation left
/// as its English placeholder, and a format specifier that does not survive translation — and all
/// three are visible in the compiled strings table.
///
/// `InfoPlist.strings` is checked alongside it, because the permission sheets are the one piece of
/// copy a driver is guaranteed to read before they have used the app at all.
///
/// **This is the shell's file today and every screen group's tomorrow.** C086–C093 add their copy to
/// all three files at once; this test is what tells them they did not.
final class LocalizationTests: XCTestCase {

    private let bundle = Bundle(for: MageRideBundleToken.self)

    private lazy var english = strings(in: "en", table: "Localizable")
    private lazy var sinhala = strings(in: "si", table: "Localizable")
    private lazy var tamil = strings(in: "ta", table: "Localizable")

    func testEveryKeyResolvesInAllThreeLocales() {
        XCTAssertFalse(english.isEmpty, "no strings found — is Localizable.strings in the bundle?")
        XCTAssertEqual(Set(english.keys), Set(sinhala.keys), "si is missing or has extra keys")
        XCTAssertEqual(Set(english.keys), Set(tamil.keys), "ta is missing or has extra keys")
    }

    func testNoTranslationIsBlank() {
        for (locale, table) in [("en", english), ("si", sinhala), ("ta", tamil)] {
            for (key, value) in table {
                XCTAssertFalse(value.trimmingCharacters(in: .whitespaces).isEmpty, "\(locale)/\(key) is blank")
            }
        }
    }

    /// Byte-identical to the English means a key was copied into si/ta and never translated — the
    /// failure mode that produces a "trilingual" app which is English in two of its three languages.
    ///
    /// Strict on purpose: the brand name appears only *inside* longer strings, never as a whole
    /// value, so there is nothing to exempt. A future component that genuinely needs an
    /// untranslatable value — a symbol, a distress signal, a glyph — should keep it as a Swift
    /// constant, which is the rule C068 set on the Android side, rather than have this test quietly
    /// allow it.
    func testNoTranslationWasLeftAsItsEnglishPlaceholder() {
        let untranslated = english.keys.filter { key in
            let en = english[key]
            return sinhala[key] == en || tamil[key] == en
        }
        XCTAssertTrue(untranslated.isEmpty, "left in English in si or ta: \(untranslated.sorted())")
    }

    func testAFormatStringKeepsItsSpecifiersInEveryLocale() {
        for (key, en) in english {
            let expected = specifiers(en)
            guard !expected.isEmpty else { continue }
            XCTAssertEqual(expected, specifiers(sinhala[key] ?? ""), "si/\(key) dropped a specifier")
            XCTAssertEqual(expected, specifiers(tamil[key] ?? ""), "ta/\(key) dropped a specifier")
        }
    }

    /// The permission sheets, which the system renders and the app never does.
    func testThePurposeStringsAreTranslated() {
        let keys = [
            "CFBundleDisplayName",
            "NSLocationWhenInUseUsageDescription",
            "NSLocationAlwaysAndWhenInUseUsageDescription",
            // C087 · SCR-DI-005. Presenting `VNDocumentCameraViewController` without this key
            // terminates the app, so it is checked alongside the location pair rather than trusted.
            "NSCameraUsageDescription",
        ]
        let en = strings(in: "en", table: "InfoPlist")
        let si = strings(in: "si", table: "InfoPlist")
        let ta = strings(in: "ta", table: "InfoPlist")

        for key in keys {
            XCTAssertNotNil(en[key], "en/InfoPlist is missing \(key)")
            XCTAssertNotNil(si[key], "si/InfoPlist is missing \(key)")
            XCTAssertNotNil(ta[key], "ta/InfoPlist is missing \(key)")
            XCTAssertNotEqual(si[key], en[key], "si/\(key) is still English")
            XCTAssertNotEqual(ta[key], en[key], "ta/\(key) is still English")
        }
    }

    /// The four tab labels are the shell's own copy and the one set a screen group cannot add to,
    /// so they get their own assertion rather than only riding the key-set comparison.
    func testTheTabBarLabelsAreLocalised() {
        for tab in DriverTab.allCases {
            XCTAssertNotNil(english[tab.labelKey], "no English label for the \(tab.rawValue) tab")
            XCTAssertNotNil(sinhala[tab.labelKey], "no Sinhala label for the \(tab.rawValue) tab")
            XCTAssertNotNil(tamil[tab.labelKey], "no Tamil label for the \(tab.rawValue) tab")
        }
    }

    /// The three languages the bundle negotiates against (AL-26's order is the onboarding screen's,
    /// not this one's).
    func testTheBundleDeclaresExactlyTheThreeLanguages() {
        let declared = Set(Bundle.main.object(forInfoDictionaryKey: "CFBundleLocalizations") as? [String] ?? [])
        XCTAssertEqual(declared, ["si", "ta", "en"])
    }

    // MARK: -

    private func strings(in locale: String, table: String) -> [String: String] {
        guard
            let path = bundle.path(forResource: locale, ofType: "lproj"),
            let localised = Bundle(path: path),
            let url = localised.url(forResource: table, withExtension: "strings"),
            let contents = NSDictionary(contentsOf: url) as? [String: String]
        else {
            XCTFail("cannot read \(locale).lproj/\(table).strings")
            return [:]
        }
        return contents
    }

    /// `%1$@`, `%2$lld` … — the positional forms `String(format:)` fills in.
    private func specifiers(_ value: String) -> Set<String> {
        let pattern = try? NSRegularExpression(pattern: #"%\d+\$[@a-zA-Z]+"#)
        let range = NSRange(value.startIndex..<value.endIndex, in: value)
        let matches = pattern?.matches(in: value, range: range) ?? []
        return Set(matches.compactMap { Range($0.range, in: value).map { String(value[$0]) } })
    }
}
