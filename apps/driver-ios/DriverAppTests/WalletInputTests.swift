import XCTest

@testable import DriverApp

/// **The two questions a keystroke becomes money through** — *is that a Driver ID?* and *how much is
/// that?*
///
/// Pure functions with no gateway and no view, which is the whole reason ``WalletInput`` exists as a
/// type rather than as four closures inside three screens.
final class WalletInputTests: XCTestCase {

    // MARK: - The Driver ID

    /// There is no `DRV-22011`. The field takes the platform id, and the pattern is the contract's.
    func testTheWireframesDriverIdIsNotAPlatformId() {
        XCTAssertFalse(WalletInput.isDriverId("DRV-22011"), "nine characters is below the Ulid minimum")
        XCTAssertTrue(WalletInput.isDriverId(testHolderId))
    }

    func testAUuidIsAcceptedAsWellAsAUlid() {
        // `_shared.yaml#/components/schemas/Ulid` runs to 36 characters precisely so a canonical UUID
        // fits: half the platform's ids are one.
        XCTAssertTrue(WalletInput.isDriverId("3f2504e0-4f89-11d3-9a0c-0305e82c3301"))
    }

    func testTheFourCrockfordExclusionsAreRejected() {
        // I, L, O and U are not in the alphabet, in either case — an id containing one is a typo for
        // 1, 1, 0 and V, and answering it at the keyboard is the whole point of validating here.
        for excluded in ["I", "L", "O", "U", "i", "l", "o", "u"] {
            let typed = String(repeating: "0", count: PlatformId.minLength - 1) + excluded
            XCTAssertFalse(WalletInput.isDriverId(typed), "\(excluded) is not Crockford base32")
        }
    }

    func testBoundsAreTheContractsOwn() {
        XCTAssertFalse(WalletInput.isDriverId(String(repeating: "0", count: PlatformId.minLength - 1)))
        XCTAssertTrue(WalletInput.isDriverId(String(repeating: "0", count: PlatformId.minLength)))
        XCTAssertTrue(WalletInput.isDriverId(String(repeating: "0", count: PlatformId.maxLength)))
        XCTAssertFalse(WalletInput.isDriverId(String(repeating: "0", count: PlatformId.maxLength + 1)))
    }

    /// A paste out of a chat app carries a trailing newline, and that is nobody's identity.
    func testAnIdIsTrimmedAndNeverRewritten() {
        XCTAssertEqual(WalletInput.driverId("  \(testHolderId)\n"), testHolderId)
        XCTAssertTrue(WalletInput.isDriverId(" \(testHolderId) "))
    }

    /// A ULID is upper-case and a UUID lower-case, so case-folding either would break the other.
    func testCaseIsNeverFolded() {
        let mixed = "01jH01dEr00000000000000001"
        XCTAssertEqual(WalletInput.driverId(mixed), mixed)
    }

    func testBlankIsNotYetRatherThanWrong() {
        XCTAssertFalse(WalletInput.isDriverId(""))
        XCTAssertFalse(WalletInput.isDriverId("   "))
    }

    // MARK: - The amount

    func testAPastedGroupSeparatorIsDroppedRatherThanRejected() {
        XCTAssertEqual(WalletInput.rupeeDigits("2,000"), "2000")
        XCTAssertEqual(WalletInput.amountMinor("2,000"), 200_000)
    }

    func testLeadingZeroesAreDropped() {
        XCTAssertEqual(WalletInput.rupeeDigits("0500"), "500")
        XCTAssertEqual(WalletInput.rupeeDigits("0000"), "")
    }

    func testAFieldStopsRatherThanOverflowing() {
        XCTAssertEqual(WalletInput.rupeeDigits(String(repeating: "9", count: 20)).count, WalletInput.maxRupeeDigits)
    }

    /// **Δ iOS.** `.keyboardType(.numberPad)` is a hint the driver can override by switching keyboards,
    /// and `Character.isNumber` is `true` for the Sinhala and Tamil digits — which `Int64.init` then
    /// refuses. A field that accepted them would show an amount it could not send.
    func testANonAsciiDigitIsNotAnAmount() {
        XCTAssertEqual(WalletInput.rupeeDigits("෧෨෩"), "")
        XCTAssertEqual(WalletInput.rupeeDigits("௧௨௩"), "")
        XCTAssertNil(WalletInput.amountMinor("෧෨෩"))
        XCTAssertEqual(WalletInput.rupeeDigits("1෧2"), "12")
    }

    /// `nil` rather than zero: "nothing typed yet" and "typed a zero" are the same disabled CTA.
    func testNothingAndZeroAreBothNil() {
        XCTAssertNil(WalletInput.amountMinor(""))
        XCTAssertNil(WalletInput.amountMinor("0"))
        XCTAssertNil(WalletInput.amountMinor("abc"))
    }

    func testARupeeFigureRoundTripsThroughTheField() {
        XCTAssertEqual(WalletInput.rupeesOf(200_000), "2000")
        XCTAssertEqual(WalletInput.amountMinor(WalletInput.rupeesOf(500_000)), 500_000)
    }
}

/// **`MoneyFormat`'s two C091 additions**, which are the only figures on this cluster that are not
/// already rupees.
final class WalletMoneyFormatTests: XCTestCase {

    /// The tier table is basis points on the wire because a percentage with a fraction cannot be an
    /// integer otherwise, and the tile has to print what Finance set.
    func testABasisPointDiscountPrintsAsAPercentage() {
        XCTAssertEqual(MoneyFormat.percentOfBps(1_000), "10%")
        XCTAssertEqual(MoneyFormat.percentOfBps(1_800), "18%")
        XCTAssertEqual(MoneyFormat.percentOfBps(1_250), "12.5%")
        XCTAssertEqual(MoneyFormat.percentOfBps(1_205), "12.05%")
        XCTAssertEqual(MoneyFormat.percentOfBps(0), "0%")
    }

    /// A negative amount is a debit on SCR-DI-025, and the sign belongs in front of the figure rather
    /// than in front of the `Rs`.
    func testASignedLedgerAmountKeepsItsPrefix() {
        XCTAssertEqual(MoneyFormat.rupees(Int64(-10_000)), "Rs -100")
        XCTAssertEqual(MoneyFormat.rupees(Int64(200_000)), "Rs 2,000")
    }
}
