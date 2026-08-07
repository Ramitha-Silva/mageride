import Foundation
import MageRideShared

/// What a driver types on SCR-DI-022, SCR-DI-023 and SCR-DI-024, and what it means.
///
/// Pure Swift and testable with no gateway, for the same reason `CropQuad` is Kotlin on the Android
/// side (C069): the two questions these screens ask — *is that a Driver ID?* and *how much is that?*
/// — are the only places a keystroke becomes money, and neither should be answered inside a `View`.
///
/// The Driver ID is the platform id and there is no `DRV-22011` — see ``PlatformId``, which is where
/// that pattern lives and which SCR-DI-028 (C092) will ask the same question of.
///
/// The same file is `apps/driver-android/.../wallet/WalletInput.kt`, function for function.
enum WalletInput {

    /// `_shared.yaml#/components/schemas/Ulid` — `minLength`.
    static let driverIdMinLength = PlatformId.minLength

    /// The same schema's `maxLength`; a UUID with its hyphens is 36.
    static let driverIdMaxLength = PlatformId.maxLength

    /// The most rupees a field accepts, as a digit count.
    ///
    /// Nine digits — Rs 999,999,999 — is far above any denomination on sale and far below where
    /// `amountMinor` (an `int64`) could overflow. It exists so a driver leaning on the keyboard gets
    /// a field that stops rather than an amount the gateway rejects.
    static let maxRupeeDigits = 9

    /// The id as it will be sent — surrounding whitespace removed, nothing else touched.
    static func driverId(_ raw: String) -> String { PlatformId.of(raw) }

    /// Whether `raw` is a well-formed platform id, once trimmed.
    static func isDriverId(_ raw: String) -> Bool { PlatformId.isValid(raw) }

    /// What a rupee field keeps of a keystroke — digits, capped at ``maxRupeeDigits``.
    ///
    /// Group separators are dropped rather than rejected so a pasted `2,000` behaves; the field
    /// renders the grouping itself. Whole rupees only, which is what every wireframe amount is and
    /// what both gateways price in.
    ///
    /// **ASCII digits only.** `Character.isNumber` is `true` for `෧` and `௧` — the Sinhala and Tamil
    /// digits a driver typing on their own keyboard can produce — and `Int64("෧")` answers `nil`, so
    /// a field that accepted them would show the driver an amount it could not send. This is not a
    /// hazard the Android twin has: `Char.isDigit` there is also true for those scripts, but a
    /// Compose `KeyboardType.Number` field never delivers them. `.keyboardType(.numberPad)` on this
    /// platform is a *hint* the driver can override by switching keyboards.
    static func rupeeDigits(_ raw: String) -> String {
        let digits = raw.filter { $0.isASCII && $0.isNumber }
        return String(digits.drop { $0 == "0" }.prefix(maxRupeeDigits))
    }

    /// `raw` as minor units, or `nil` when there is no positive amount in it.
    ///
    /// `nil` rather than zero: "nothing typed yet" and "typed a zero" are the same disabled CTA, and
    /// neither is an amount worth sending.
    static func amountMinor(_ raw: String) -> Int64? {
        let digits = rupeeDigits(raw)
        guard !digits.isEmpty, let rupees = Int64(digits), rupees > 0 else { return nil }
        return rupees * minorUnits
    }

    /// ``amountMinor(_:)`` as `Money`, for the `:shared` rules that compare against a balance.
    static func amount(_ raw: String) -> Money? {
        amountMinor(raw).map { Money.companion.ofMinor(amountMinor: $0) }
    }

    /// A whole-rupee amount back as the digits a field holds — how a voucher tile fills the box.
    static func rupeesOf(_ amountMinor: Int64) -> String { String(amountMinor / minorUnits) }

    private static let minorUnits: Int64 = 100
}
