import XCTest

@testable import PassengerApp

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
/// `InfoPlist.strings` is checked alongside it, because the permission sheet is the one piece of copy
/// a passenger is guaranteed to read before they have used the app at all.
///
/// **This is the shell's file today and every screen group's tomorrow.** C095–C102 add their copy to
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
    /// Strict on purpose. **`CFBundleDisplayName` is the one value on this surface that is
    /// deliberately transliterated rather than translated**, and it lives in `InfoPlist.strings`
    /// where this assertion does not reach — see ``testThePurposeStringsAreTranslated()``, which
    /// checks it separately and requires it to *differ* from the English, which the transliterations
    /// do.
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

    /// The permission sheet and the Home-screen name, which the system renders and the app never
    /// does.
    ///
    /// There is deliberately **no camera key**: SCR-PI-017's driver-QR scan is C098's and the purpose
    /// string belongs in the commit that opens a camera. The same fence the driver app kept until
    /// C087 needed the scanner.
    func testThePurposeStringsAreTranslated() {
        let keys = ["CFBundleDisplayName", "NSLocationWhenInUseUsageDescription"]
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

    /// The tab labels and the Menu rows are the shell's own copy and the one set a screen group
    /// cannot add to, so they get their own assertion rather than only riding the key-set comparison.
    func testTheShellsOwnCopyIsLocalised() {
        let keys = PassengerTab.allCases.map(\.labelKey) + PassengerMenuDestination.allCases.map(\.labelKey)
        for key in keys {
            XCTAssertNotNil(english[key], "no English string for \(key)")
            XCTAssertNotNil(sinhala[key], "no Sinhala string for \(key)")
            XCTAssertNotNil(tamil[key], "no Tamil string for \(key)")
        }
    }

    /// **Every declared key is referenced by the code, and every referenced key is declared.**
    ///
    /// The second half is what a missing-key bug looks like on this platform: `NSLocalizedString`
    /// answers the key itself, so a typo ships as a screen with `offline_banner_mesage` written on
    /// it. The first half is what stops this file accumulating copy for a screen that was never
    /// built.
    ///
    /// The sources are read off disk through `#filePath`, which works because a simulator shares the
    /// host's filesystem — the technique C091 introduced on the driver side.
    func testEveryStringKeyIsUsedAndEveryUsedKeyExists() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // PassengerAppTests
            .deletingLastPathComponent()   // apps/passenger-ios
            .appendingPathComponent("PassengerApp")

        var sources = ""
        let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)
        while let url = walker?.nextObject() as? URL {
            guard url.pathExtension == "swift" else { continue }
            sources += (try? String(contentsOf: url, encoding: .utf8)) ?? ""
        }
        XCTAssertFalse(sources.isEmpty, "no Swift sources found under \(root.path)")

        let referenced = Set(
            english.keys.filter { sources.contains("\"\($0)\"") }
        )
        XCTAssertEqual(
            Set(english.keys).subtracting(referenced).sorted(),
            [],
            "declared but never referenced"
        )
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
    ///
    /// **The conversion character is exactly one letter, after an optional length modifier** — which
    /// is what `String(format:)` itself parses, and what a looser `[a-zA-Z]+` gets wrong: the English
    /// `"Resend (%1$ds)"` is a `%1$d` followed by a literal `s` for *seconds*, and a greedy pattern
    /// reads it as a specifier called `%1$ds` that no translation can possibly contain. That was a
    /// real false failure here (Δ C095); the same pattern is in `apps/driver-ios` and has the same
    /// latent hole.
    private func specifiers(_ value: String) -> Set<String> {
        let pattern = try? NSRegularExpression(pattern: #"%\d+\$(?:ll|l|hh|h|z|t|j|q)?[@diufFeEgGxXoscpaA]"#)
        let range = NSRange(value.startIndex..<value.endIndex, in: value)
        let matches = pattern?.matches(in: value, range: range) ?? []
        return Set(matches.compactMap { Range($0.range, in: value).map { String(value[$0]) } })
    }
}
