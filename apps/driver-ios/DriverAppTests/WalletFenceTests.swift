import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// The fences C091's prompt draws, enforced rather than remembered.
///
/// > *"Parity-fenced to C073. No bank transfer, no web portal, no commission line, Driver-ID-only
/// > credit requests."*
///
/// Some of that is checkable as a fact about types; the rest — *"no bank-transfer option appears
/// anywhere"* — is only checkable as a fact about the **source and the copy**, which is what
/// `MoneyDomainHygieneTest` does for `:shared`'s half of the same rule and `WalletFenceTest` for the
/// Android app's. A screen is where a removed payment method comes back, because a screen is where
/// somebody adds a tile — and a translation nobody on the review reads is where it comes back quietly.
///
/// **The copy is read out of the built bundle and the source off disk**, which are two different
/// mechanisms because they answer two different questions. `Localizable.strings` is a resource and
/// ``LocalizationTests`` already reads it that way; the Swift sources are not in any bundle, so they
/// are found relative to `#filePath` — a compile-time literal pointing at the checkout the tests were
/// compiled from.
///
/// **That works because a simulator shares the host's filesystem**, and the verify command is a
/// simulator destination (`apps/driver-ios/CLAUDE.md`). It would not work on a device, and the two
/// source-scanning tests below would fail there with *"the wallet cluster is not where this test
/// looks"* rather than pass silently — which is the right way round for a fence.
///
/// **Comments are stripped before the source is searched**, for the reason the Android twin gives:
/// half of this cluster's job is to *document* why bank transfer is absent, and a check that fired on
/// the explanation would push the explanation out of the code.
final class WalletFenceTests: XCTestCase {

    // MARK: - AL-05 · three rails and no fourth

    /// AL-05 removed the route rather than deprecating it: `wallet.yaml` carries no
    /// `POST /v1/wallet/topup/bank-transfer`, `WalletApi` has no such call, and `TopupMethod` has no
    /// fourth entry a segment could select.
    ///
    /// The app's own ``TopupMethods/all`` table is asserted against the shared enum's `ordinal`s in the
    /// same breath, because the table is what SCR-DI-022 actually draws — a method added to `:shared`
    /// must fail here rather than quietly miss a segment.
    func testThereAreExactlyThreeTopUpMethodsAndNoneOfThemIsABankTransfer() {
        XCTAssertEqual(
            TopupMethods.all.map(\.ordinal),
            [0, 1, 2],
            "the segments are the enum's own order, and there are three of them"
        )
        XCTAssertEqual(TopupMethods.all.map(\.name), ["ONEPAY_CARD", "ONEPAY_WALLET", "LANKAQR"])
        XCTAssertEqual(Set(TopupMethods.all.map(\.labelKey)).count, 3, "three segments, three labels")
    }

    func testNoSourceFileInThisClusterCarriesABankTransferTopUpPath() throws {
        let offenders = try walletSources().flatMap { file, code in
            Self.bankTransferInCode.filter(code.contains).map { "\(file): \($0)" }
        }

        XCTAssertTrue(
            offenders.isEmpty,
            "AL-05 — top-up is OnePay card / OnePay wallet / LankaQR only: \(offenders)"
        )
    }

    /// The Sinhala and Tamil files are where a removed method is likeliest to survive a review: nobody
    /// reading the English screen would see it.
    func testNoWalletCopyOffersABankTransferInAnyOfTheThreeLanguages() {
        for (locale, values) in walletCopy() {
            XCTAssertFalse(values.isEmpty, "no wallet copy in \(locale)")
            let offenders = values.filter { value in
                Self.bankTransferInCopy.contains { value.localizedCaseInsensitiveContains($0) }
            }
            XCTAssertTrue(offenders.isEmpty, "\(locale) offers a bank transfer: \(offenders)")
        }
    }

    // MARK: - AL-34 · Driver ID only

    /// AL-34 removed SCR-DI-023's QR-scan path. ``DocumentCaptureCoordinator`` is C087's scanner seam
    /// and the only camera door in the app; a wallet screen reaching for it would be that path coming
    /// back.
    ///
    /// **On this platform the fence is stronger than the Android one and it is worth saying why.** The
    /// Android app carries `com.google.zxing:core`, whose reader half C074 went on to use for
    /// SCR-DA-027 — so *"nothing here scans"* is a rule about which classes a package imports. Here the
    /// AL-15 encoder is `CIQRCodeGenerator`, a first-party **writer**, and no decoder is linked into
    /// the target at all: `AVCaptureMetadataOutput` and `CIDetector` are the two doors, and neither is
    /// named anywhere in this cluster.
    func testNothingInThisClusterScansACodeToFindADriver() throws {
        let offenders = try walletSources().flatMap { file, code in
            Self.scannerSeams.filter(code.contains).map { "\(file): \($0)" }
        }

        XCTAssertTrue(offenders.isEmpty, "credit requests are Driver-ID only (AL-34): \(offenders)")
    }

    // MARK: - AL-01 · the exact value, and no commission

    /// Not a configured rate of zero — there is no journal kind a commission could post under, and
    /// `entryFor` produces two postings that sum to zero (AL-01, D-09).
    func testATransferCarriesNoFeeLegAtAnyAmount() {
        XCTAssertEqual(CreditTransferRules.shared.COMMISSION_BPS, 0)

        for minor in [Int64(1), 50_000, 100_000, 1_000_000, 99_999_999_900] {
            let amount = Money.companion.ofMinor(amountMinor: minor)
            XCTAssertEqual(CreditTransferRules.shared.debitedFromSender(amount: amount).amountMinor, minor)
            XCTAssertEqual(CreditTransferRules.shared.creditedToRecipient(amount: amount).amountMinor, minor)
            XCTAssertEqual(CreditTransferRules.shared.feeFor(amount: amount).amountMinor, 0)
        }
    }

    /// *"Never render a commission line."* The wireframe's own history row says *"Reseller transfer"*,
    /// which is the trap: AL-01 makes the margin the purchase discount, taken once at purchase, and
    /// there is nothing to take off a transfer to name. `wallet_kind_transfer` is *"Credit transfer"*.
    ///
    /// **The word *reseller* is deliberately not on the list**, in any of the three languages: two of
    /// the wireframe's own strings *deny* the concept (*"no special reseller number"*), and a fence
    /// that fired on the denial would delete the sentence that tells a driver AL-01 is true. What is
    /// forbidden is a **fee**, which is a thing nothing on this platform can move.
    func testNoWalletCopyNamesACommission() {
        for (locale, values) in walletCopy() {
            let offenders = values.filter { value in
                Self.commissionInCopy.contains { value.localizedCaseInsensitiveContains($0) }
            }
            XCTAssertTrue(offenders.isEmpty, "\(locale) names a commission or a reseller: \(offenders)")
        }
    }

    // MARK: - AL-05 · everything is in-app

    /// *"There is no driver web portal"* (AL-05). The one URL this cluster mints is OnePay's return
    /// leg, and it is declared in one file — ``PaymentReturn`` — on the payment host
    /// `DriverApp.entitlements` already authorises. Every other link a driver follows from here came
    /// from the gateway in a response, which is what makes *"nowhere else spells a URL"* the checkable
    /// form of the rule.
    func testTheOnlyUrlThisClusterMintsIsThePaymentReturnLeg() throws {
        let offenders = try walletSources()
            .filter { $0.file != "PaymentHandoff.swift" && $0.code.contains("\"http") }
            .map(\.file)

        XCTAssertTrue(offenders.isEmpty, "AL-05 — everything is in-app: \(offenders)")

        XCTAssertTrue(PaymentReturn.url.hasPrefix("https://\(PaymentReturn.host)"))
        XCTAssertTrue(PaymentReturn.isReturn(URL(string: "https://\(PaymentReturn.host)/x?status=ok")!))
        XCTAssertFalse(
            PaymentReturn.isReturn(URL(string: "https://checkout.onepay.lk/session/abc")!),
            "the gateway's own page is not the return leg"
        )
    }

    // MARK: - The five screens

    /// SCR-DI-021 **is** tab 3 and the other four are pushed onto its stack, which is what makes the
    /// system back button say `‹ Wallet` on each of them — the label every one of those cells draws.
    func testTheFiveWalletScreensAreRegisteredAndOnlyTheFirstIsATabRoot() {
        let pushed: [DriverRoute] = [.walletTopUp, .walletRequestCredit, .walletTransfer, .walletHistory]
        let roots = Set(DriverTab.allCases.map(\.route))

        for route in pushed + [.wallet] {
            XCTAssertTrue(DriverRoute.staticRoutes.contains(route), "\(route.path) is not registered")
            XCTAssertEqual(route.tab, .wallet, "\(route.path) hangs off the wrong stack")
            XCTAssertFalse(route.isFullScreenTakeover)
            XCTAssertFalse(route.isPreSession)
        }

        XCTAssertEqual(DriverTab.wallet.route, .wallet, "SCR-DI-021 IS tab 3")
        for route in pushed {
            XCTAssertFalse(roots.contains(route), "\(route.path) is pushed, not a tab root")
        }
    }

    /// The four chips the wireframe draws, and the ledger kinds each keeps.
    func testTheHistoryChipsAreTheWireframesFourAndTheirKindsDoNotOverlap() {
        XCTAssertEqual(HistoryFilter.allCases, [.all, .fees, .topUps, .transfers])
        XCTAssertTrue(HistoryFilter.all.kinds.isEmpty, "All keeps a kind this build has not heard of")

        let named = HistoryFilter.allCases.filter { $0 != .all }.map(\.kinds)
        for (index, kinds) in named.enumerated() {
            XCTAssertFalse(kinds.isEmpty)
            for other in named[(index + 1)...] {
                XCTAssertTrue(kinds.isDisjoint(with: other), "a line cannot be under two chips")
            }
        }
    }

    // MARK: - Reading the cluster

    /// Every Swift file under `DriverApp/Wallet`, with its comments removed.
    private func walletSources() throws -> [(file: String, code: String)] {
        let directory = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()      // DriverAppTests
            .deletingLastPathComponent()      // apps/driver-ios
            .appendingPathComponent("DriverApp/Wallet", isDirectory: true)

        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            .filter { $0.hasSuffix(".swift") }
            .sorted()

        XCTAssertFalse(names.isEmpty, "the wallet cluster is not where this test looks: \(directory.path)")

        return try names.map { name in
            let text = try String(contentsOf: directory.appendingPathComponent(name), encoding: .utf8)
            return (name, WalletFenceTests.stripComments(text))
        }
    }

    /// `text` with its block and line comments removed.
    private static func stripComments(_ text: String) -> String {
        var stripped = text.replacingOccurrences(
            of: #"/\*[\s\S]*?\*/"#,
            with: " ",
            options: .regularExpression
        )
        stripped = stripped.replacingOccurrences(of: #"(?m)//.*$"#, with: " ", options: .regularExpression)
        return stripped
    }

    /// Every `wallet_*` value in each of the three `Localizable.strings`, keyed by locale.
    private func walletCopy() -> [String: [String]] {
        let bundle = Bundle(for: MageRideBundleToken.self)
        var copy: [String: [String]] = [:]
        for locale in ["en", "si", "ta"] {
            guard
                let path = bundle.path(forResource: locale, ofType: "lproj"),
                let localised = Bundle(path: path),
                let url = localised.url(forResource: "Localizable", withExtension: "strings"),
                let table = NSDictionary(contentsOf: url) as? [String: String]
            else {
                XCTFail("cannot read \(locale).lproj/Localizable.strings")
                continue
            }
            copy[locale] = table.filter { $0.key.hasPrefix("wallet_") }.map(\.value)
        }
        return copy
    }

    /// Spellings a bank-transfer **top-up** would take in code.
    ///
    /// Deliberately not the bare words "bank" or "transfer" — ``PaymentHandoff`` opens a bank app for
    /// LankaQR and a driver-to-driver credit movement is a transfer, and both are legitimate. Same list
    /// `MoneyDomainHygieneTest` and `WalletFenceTest` use, for the same reason.
    private static let bankTransferInCode = [
        "bankTransfer", "BANK_TRANSFER", "bank_transfer", "topup/bank", "BankTransfer",
    ]

    /// The same method as a driver would read it. Copy, not code, so the prose forms matter.
    private static let bankTransferInCopy = ["bank transfer", "bank deposit", "බැංකු මාරු", "வங்கி பரிமாற்ற"]

    /// A per-transfer margin, in the three languages. See the test for why *reseller* is not here.
    private static let commissionInCopy = ["commission", "කොමිස්", "கமிஷன்"]

    /// The camera doors this app has, plus the two decoders it deliberately does not link.
    private static let scannerSeams = [
        "DocumentCaptureCoordinator",
        "DocumentCaptureTarget",
        "AVCaptureMetadataOutput",
        "CIDetector",
        "VNDocumentCameraViewController",
        "DataScannerViewController",
    ]
}
