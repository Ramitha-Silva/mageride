import Foundation
import MageRideShared

/// A `GET /v1/mode-b/files/{kind}/{id}?expires=&signature=` link, taken apart.
///
/// **This app has no image loader, and adding one to render a single QR would be the wrong trade.**
/// The contract's own words are *"the URL goes to an image loader, which carries no bearer"*, and
/// `security: []` is on that route precisely so an `<img src>` works. What this target has instead is
/// C013's typed client, which already knows the gateway origin, the retry curve, the circuit breaker
/// and the RFC 7807 mapping — so the link is split back into the four values
/// `SubscriptionApi.getModeBFile` takes and fetched through the same stack as everything else.
///
/// **The origin in the link is discarded on purpose.** The call is re-issued against
/// ``PassengerEnvironment``'s configured gateway, so a signed URL minted with an internal or stale
/// host cannot redirect this app anywhere — the only things taken from it are the file's identity and
/// the signature, which is the credential.
///
/// Parsed by hand rather than with `URLComponents`: a **relative** link has no scheme, and the two
/// values that matter sit in a query string this app must read whether or not Foundation is willing
/// to call the whole string a URL. The same call `apps/passenger-android` made about `android.net.Uri`
/// — and this table is asserted, so the parse has to be the one under test.
struct SignedFileLink: Equatable {

    /// Which document — the owner's LankaQR, or a transfer slip.
    let kind: ModeBFileKind

    /// The payout profile (`lankaqr`) or the payment (`slips`).
    let id: String

    /// Unix seconds the signature is good until; the server, not the client, enforces it.
    let expires: Int64

    /// The HMAC over `(kind, id, expires)`.
    let signature: String

    /// The path prefix every Mode B document link carries.
    static let path = "/v1/mode-b/files/"

    /// Reads `link`, or answers `nil` when it is not a Mode B file link this build understands.
    ///
    /// `nil` rather than a thrown error for every failure — a malformed link is a server or a version
    /// problem, and the screen's answer to both is the same: draw the transfer details it does have
    /// and say the QR is unavailable. A thrown parse error would turn a missing image into a failed
    /// payment.
    static func parse(_ link: String?) -> SignedFileLink? {
        let value = link?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard let prefix = value.range(of: SignedFileLink.path) else { return nil }

        let remainder = String(value[prefix.upperBound...])
        let path = remainder.prefix { $0 != "?" }
        let query = remainder.dropFirst(path.count).dropFirst()

        let segments = path.split(separator: "/", omittingEmptySubsequences: false)
        guard segments.count >= SignedFileLink.segmentCount else { return nil }

        let id = String(segments[1])
        guard let kind = SignedFileLink.kind(wire: String(segments[0])), !id.isEmpty else { return nil }

        var parameters: [String: String] = [:]
        for pair in query.split(separator: "&") {
            let name = pair.prefix { $0 != "=" }
            let raw = pair.dropFirst(name.count).dropFirst()
            guard !name.isEmpty, !raw.isEmpty else { continue }
            parameters[String(name)] = String(raw)
        }

        guard
            let expires = parameters[SignedFileLink.expiresParameter].flatMap(Int64.init),
            let signature = parameters[SignedFileLink.signatureParameter]
        else {
            return nil
        }

        return SignedFileLink(kind: kind, id: id, expires: expires, signature: signature)
    }

    /// The two kinds `subscription.yaml` declares, matched on their wire spelling.
    ///
    /// Written as two comparisons against the singletons rather than a walk over `entries`, which is
    /// the idiom this app already uses everywhere it branches on an exported Kotlin enum (see
    /// ``ModeToken/forMode(_:)``): an entry is one object and `==` is `isEqual:` over it. A kind a
    /// later contract adds answers `nil` here, which is the state the pay sheet already draws.
    private static func kind(wire: String) -> ModeBFileKind? {
        if wire == ModeBFileKind.lankaqr.wire { return ModeBFileKind.lankaqr }
        if wire == ModeBFileKind.slips.wire { return ModeBFileKind.slips }
        return nil
    }

    /// `{kind}` and `{id}` — the two path segments after the prefix.
    private static let segmentCount = 2
    private static let expiresParameter = "expires"
    private static let signatureParameter = "signature"
}
