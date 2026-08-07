import Foundation

/// What SCR-DI-027 accepts in its IMEI field, and what a device QR code means.
///
/// Pure Swift and testable on this host, for the same reason ``PlatformId`` and ``WalletInput`` are:
/// the two questions this screen asks — *is that an IMEI?* and *which fifteen digits in that QR
/// payload are one?* — decide what gets bound to a vehicle, and neither should be answered inside a
/// `View` or inside a type that needs a camera.
///
/// ### The pattern is the contract's, not a guess
///
/// `provisioning.yaml#/components/schemas/Imei` and `registry.yaml`'s copy of it are both `^\d{15}$`
/// — fifteen digits, nothing else. There is deliberately **no Luhn check** here even though the
/// fifteenth digit of a real IMEI is one: neither contract asks for it, `prov.trackers` does not
/// enforce it, and a client that refused a check digit the server would have accepted would make a
/// perfectly good tracker unpairable at the roadside with no way round it.
///
/// The same file is `apps/driver-android/.../tracker/TrackerImei.kt`, function for function. The one
/// difference is where a non-ASCII digit is refused: Kotlin's `Char.isDigit` is the whole `Nd`
/// category, so the Android field *keeps* a Devanagari digit and the validator then rejects it, while
/// ``digits(_:)`` here drops it at the keystroke. Both refuse to pair; this is ``PhoneNumber``'s rule
/// applied to the other serial a driver types, and for the same reason — an IMEI is matched against
/// `^\d{15}$` on the wire.
enum TrackerImei {

    /// The contract's length. Fifteen digits, and the field stops there.
    static let length = 15

    /// How many digits are printed per group in ``grouped(_:)`` — the wireframe's `8612 3456 …`.
    private static let group = 4

    /// What the field keeps of a keystroke — digits only, capped at ``length``.
    ///
    /// Separators are dropped rather than rejected so an IMEI copied off a sticker as
    /// `8612 3456 7890 123` or `861234-56-789012-3` behaves; the screen renders the grouping itself
    /// through ``grouped(_:)``.
    static func digits(_ raw: String) -> String {
        String(raw.filter { $0.isASCII && $0.isNumber }.prefix(length))
    }

    /// Whether `raw` is a well-formed IMEI once reduced to digits.
    static func isValid(_ raw: String) -> Bool { digits(raw).count == length }

    /// `861234567890123` → `8612 3456 7890 123`, for reading a paired device's id back.
    static func grouped(_ imei: String) -> String {
        var chunks: [String] = []
        var rest = Substring(digits(imei))
        while !rest.isEmpty {
            chunks.append(String(rest.prefix(group)))
            rest = rest.dropFirst(group)
        }
        return chunks.joined(separator: " ")
    }

    /// The IMEI inside a scanned device QR code, or `nil` when there is not exactly one.
    ///
    /// **No format is specified anywhere.** D2' §SCR-DI-027 says the QR carries the *"device QR"* and
    /// provisioning-svc's `method` enum has a `qr` value, but no spec, contract or DB column says what
    /// the tracker vendor prints in it. In the field it is one of three things: the bare IMEI, a
    /// `key:value` line (`IMEI:861234567890123`), or a provisioning URL with the IMEI in a query
    /// parameter — so this looks for the number rather than for a shape, and refuses a payload
    /// carrying **two** different candidates instead of picking the first. A scan that cannot be read
    /// leaves the driver typing, which is the path that always works.
    ///
    /// The lookarounds are what stop a payload carrying a 20-digit ICCID from yielding its first
    /// fifteen characters as an "IMEI" — a serial that would bind, mint a credential and then never
    /// connect.
    static func imeiIn(_ payload: String) -> String? {
        let range = NSRange(payload.startIndex..<payload.endIndex, in: payload)
        let matches = candidate?.matches(in: payload, range: range) ?? []

        var found: [String] = []
        for match in matches {
            guard let span = Range(match.range, in: payload) else { continue }
            let value = String(payload[span])
            if !found.contains(value) { found.append(value) }
        }
        return found.count == 1 ? found[0] : nil
    }

    /// `(?<!\d)\d{15}(?!\d)` — fifteen digits that are not part of a longer run.
    ///
    /// An `NSRegularExpression` where ``PlatformId`` takes a `CharacterSet`, because this rule really
    /// is a pattern: the lookarounds are the whole point and there is no character-class-plus-length
    /// form of them. It is compiled once — a QR payload is decoded on a camera frame.
    private static let candidate = try? NSRegularExpression(pattern: "(?<!\\d)\\d{\(length)}(?!\\d)")
}
