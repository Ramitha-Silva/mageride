import XCTest

@testable import DriverApp

/// `+947XXXXXXXX` (D5' §14.1), and the four ways a driver writes it down.
///
/// The same cases as `apps/driver-android/.../onboarding/PhoneNumberTest.kt`. A number that
/// normalises one way here and another way there is a driver who cannot sign in on their second
/// handset, which is exactly the class of divergence the parity fence exists for.
final class PhoneNumberTests: XCTestCase {

    func testATrunkZeroIsDropped() {
        XCTAssertEqual(PhoneNumber.normalise("0771234567"), "771234567")
    }

    func testACountryCodeIsDropped() {
        XCTAssertEqual(PhoneNumber.normalise("+94771234567"), "771234567")
        XCTAssertEqual(PhoneNumber.normalise("0094771234567"), "771234567")
    }

    /// A pasted number arrives with whatever separators the sender used.
    func testSeparatorsAreStripped() {
        XCTAssertEqual(PhoneNumber.normalise("+94 77 123 4567"), "771234567")
        XCTAssertEqual(PhoneNumber.normalise("077-123-4567"), "771234567")
    }

    func testTheFieldCannotHoldMoreThanTheNationalNumber() {
        XCTAssertEqual(PhoneNumber.normalise("77123456789999").count, PhoneNumber.nationalLength)
    }

    /// `%d` is not a digit in every script, and an E.164 string is built out of what survives here.
    func testOnlyAsciiDigitsSurvive() {
        XCTAssertEqual(PhoneNumber.normalise("77१२३4567"), "774567")
    }

    func testAValidNumberIsNineDigitsStartingWithSeven() {
        XCTAssertTrue(PhoneNumber.isValid("771234567"))
        XCTAssertFalse(PhoneNumber.isValid("71234567"), "eight digits")
        XCTAssertFalse(PhoneNumber.isValid("112345678"), "a landline, not a mobile")
        XCTAssertFalse(PhoneNumber.isValid(""))
    }

    func testTheE164FormIsWhatTheContractTakes() {
        XCTAssertEqual(PhoneNumber.toE164("771234567"), "+94771234567")
    }

    /// The mask is the same characters in all three languages, which is why it is a constant rather
    /// than a string resource — `LocalizationTests` reads three identical values as a translation
    /// nobody did.
    func testThePlaceholderIsTheWireframesMask() {
        XCTAssertEqual(PhoneNumber.placeholder, "7X XXX XXXX")
        XCTAssertEqual(PhoneNumber.countryCode, "+94")
    }
}
