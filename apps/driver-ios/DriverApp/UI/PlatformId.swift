import Foundation

/// What the platform means by an id, and what a field that takes one accepts.
///
/// **There is no `DRV-22011` and no `PAX-90431`.** Both appear in the wireframes — the first on
/// SCR-DI-023/024, the second on SCR-DI-028 — and neither exists. Every route that takes a user is
/// declared `_shared.yaml#/components/schemas/Ulid`: 26–36 characters of the Crockford alphabet plus
/// the hyphen a canonical UUID carries. Neither `iam.users` nor `registry.driver_profiles` has a
/// human-readable code column, and **no route on the platform resolves one form into the other** —
/// the only driver directory is `GET /admin/directories/drivers`, which is an internal-role read.
/// Recorded as a spec gap by C073 for the driver id and again by C074 for the passenger id.
///
/// So a field that asks for a "Driver ID" takes the platform id, checked here against the contract's
/// own pattern so a mistyped one is answered at the keyboard rather than by a `404` after an attested
/// POST. SCR-DI-029 (C092) is the screen that shows a driver their own id, which is how one gets
/// into another driver's hands; **if that screen does not print it verbatim and copyably, credit
/// transfer has no way to be used.**
///
/// The value is **trimmed and never rewritten**: `Primitives.kt` is explicit that a client which
/// silently reshaped a server-supplied identifier is a worse failure than one that passed it
/// through, and a ULID is upper-case while a UUID is lower-case — case-folding either breaks the
/// other. Trimming is safe and necessary: a paste out of a chat app carries a trailing newline, and
/// that is nobody's identity.
///
/// The same file is `apps/driver-android/.../ui/PlatformId.kt`, function for function. ``WalletInput``
/// is the wallet cluster's door onto it, exactly as it is there.
enum PlatformId {

    /// `_shared.yaml#/components/schemas/Ulid` — `minLength`.
    static let minLength = 26

    /// The same schema's `maxLength`; a UUID with its hyphens is 36.
    static let maxLength = 36

    /// The id as it will be sent — surrounding whitespace removed, nothing else touched.
    static func of(_ raw: String) -> String {
        raw.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Whether `raw` is a well-formed platform id, once trimmed.
    static func isValid(_ raw: String) -> Bool {
        let trimmed = of(raw)
        guard trimmed.count >= minLength, trimmed.count <= maxLength else { return false }
        return trimmed.unicodeScalars.allSatisfy(allowed.contains)
    }

    /// The contract's charset: Crockford base32 — the digits and the letters minus `I`, `L`, `O` and
    /// `U` — in either case, plus the hyphen a canonical UUID carries.
    ///
    /// A `CharacterSet` rather than the Android twin's anchored `Regex`, because the pattern there is
    /// a character class and a length bound and nothing else: `NSRegularExpression` would compile the
    /// same rule into a form that is slower to run on every keystroke and harder to read. The two
    /// agree character for character, which ``WalletInputTests`` asserts on the excluded four.
    private static let allowed = CharacterSet(
        charactersIn: "0123456789ABCDEFGHJKMNPQRSTVWXYZabcdefghjkmnpqrstvwxyz-"
    )
}
