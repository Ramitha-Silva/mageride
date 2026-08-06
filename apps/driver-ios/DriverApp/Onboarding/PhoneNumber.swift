import Foundation

/// The one phone shape this platform accepts: `+947XXXXXXXX` (D5' §14.1).
///
/// Sri Lanka is the only country MageRide operates in, so `+94` is a **prefix on the field**
/// rather than a country picker — every other dialling code is rejected downstream anyway. What is
/// typed is the national number, and the two ways a Sri Lankan number is written down (`0771234567`
/// with the trunk zero, `771234567` without) both have to work: a driver reading their number off
/// the back of their own phone will type the first.
///
/// Byte-for-byte `apps/driver-android/.../onboarding/PhoneNumber.kt`. The normalisation is the same
/// four steps in the same order, because a number that is valid on one app and not the other is a
/// driver who cannot sign in on their second handset.
enum PhoneNumber {

    /// The dialling code shown as the field's prefix.
    static let countryCode = "+94"

    /// The wireframe's `7X XXX XXXX` hint.
    ///
    /// A digit mask, not copy: it is the same characters in Sinhala, Tamil and English, so putting
    /// it in the three `Localizable.strings` files would mean three identical values —
    /// `LocalizationTests` reads that (correctly) as a translation nobody did.
    static let placeholder = "7X XXX XXXX"

    /// National significant number length — nine digits, the first of which is always `7`.
    static let nationalLength = 9

    private static let mobileLeadingDigit: Character = "7"

    /// Strips everything a driver might type around the digits, and the trunk `0`.
    ///
    /// Applied on every keystroke rather than on submit, so the field can never hold a value the
    /// validator would reject — which is what makes a pasted `+94 77 123 4567` simply work.
    static func normalise(_ input: String) -> String {
        // ASCII only, where Kotlin's `Char.isDigit` is the whole `Nd` category. An E.164 string is
        // built out of this, and `+94७७…` is not a phone number the gateway can dial — a keyboard
        // that produces another script's digits should fail the field, not the request.
        let digits = input.filter { $0.isASCII && $0.isNumber }
        // Leading zeros first: that clears both the trunk `0` and the `00` international prefix,
        // after which `94` can only be the country code and never the start of a mobile number
        // (every one of those starts with a 7).
        var national = String(digits.drop(while: { $0 == "0" }))
        if national.hasPrefix("94") { national.removeFirst(2) }
        return String(national.prefix(nationalLength))
    }

    /// Whether [national] is a complete Sri Lankan mobile number.
    static func isValid(_ national: String) -> Bool {
        national.count == nationalLength && national.first == mobileLeadingDigit
    }

    /// The E.164 form `POST /v1/auth/otp/request` takes. Only call this on an ``isValid(_:)`` number.
    static func toE164(_ national: String) -> String { countryCode + national }
}
