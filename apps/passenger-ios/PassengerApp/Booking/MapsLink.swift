import Foundation
import MageRideShared

/// What a pasted string turned out to be.
///
/// Three outcomes rather than an optional point, because the middle one is a *different action*: a
/// short link is a perfectly good link that this device cannot read, and answering `nil` for it
/// would send the passenger to *"pick on map"* when one HTTP call would have worked.
enum MapsLinkParse: Equatable {

    /// Coordinates were in the URL. No network needed.
    case resolved(lat: Double, lng: Double)

    /// A shortened link — `maps.app.goo.gl` or `goo.gl/maps`.
    ///
    /// The coordinates are behind a redirect, and following redirects is transit-svc's job
    /// (`GET /v1/geo/parse-maps-link`): a short link is resolved server-side *"via a single
    /// server-side HTTP redirect follow … no Google API"* (D6' §I-23.1).
    case needsServer(url: String)

    /// Not a link this platform understands. SCR-PI-012a's Error state — *"pick on map"*.
    case unreadable
}

/// AL-20's Google-Maps paste, parsed on the device.
///
/// D5' §BR-23.4 and D6' §I-23.1 both say the same thing and it is the whole design: **full URLs are
/// parsed client-side, short links go to transit-svc.** No Google SDK, no API key, no billing
/// relationship — a Google Maps URL is a string, and the coordinates in it are already there.
///
/// The four forms the specs enumerate, in the order this type tries them:
///
/// | Form | Example | Why here in the order |
/// |---|---|---|
/// | `!3d…!4d…` | `…/data=!3m1!4b1!4d79.86!3d6.93` | the **place's own** coordinate |
/// | `q=` / `query=` | `?q=6.93,79.86` | an explicit request for a point |
/// | `ll=` / `center=` | `?ll=6.93,79.86` | an explicit map centre |
/// | `@lat,lng,zoom` | `/maps/@6.93,79.86,15z` | the **viewport**, which is not the pin |
///
/// **`!3d!4d` beats `@`, and that is not arbitrary.** A `/maps/place/…` URL usually carries both:
/// `@` is where the camera was and `!3d!4d` is where the *place* is, and they are routinely a
/// hundred metres apart because the map was panned. Preferring the viewport would drop a pickup pin
/// on whatever was in the middle of the sender's screen. `q=` outranks `@` for the same reason.
///
/// **A `q=` that is not a coordinate is not a failure of this parser** — `?q=Colombo+Fort` is a
/// search term, and the caller falls through to the next form and then to the server.
///
/// Pure Swift over `NSRegularExpression`: it is the same four patterns
/// `apps/passenger-android/.../booking/MapsLink.kt` uses, and `MapsLinkTests` runs the same corpus.
enum MapsLink {

    /// Reads `input` as far as this device can.
    static func parse(_ input: String) -> MapsLinkParse {
        let url = input.trimmed
        guard !url.isEmpty else { return .unreadable }
        guard googleHosts.contains(where: { url.range(of: $0, options: .caseInsensitive) != nil }) else {
            return .unreadable
        }

        if let point = coordinates(in: url) {
            return .resolved(lat: point.0, lng: point.1)
        }
        if shortHosts.contains(where: { url.range(of: $0, options: .caseInsensitive) != nil }) {
            return .needsServer(url: url)
        }
        return .unreadable
    }

    /// The first coordinate pair any of the four forms yields, or `nil`.
    ///
    /// Separate from ``parse(_:)`` so the ordering above is one readable expression rather than four
    /// nested `if`s, and so a test can ask *"what did this URL contain"* without a verdict attached.
    private static func coordinates(in url: String) -> (Double, Double)? {
        placePin(in: url)
            ?? parameterPair(in: url, keys: queryKeys)
            ?? parameterPair(in: url, keys: centreKeys)
            ?? viewport(in: url)
    }

    /// `!3d<lat>!4d<lng>` — the place pin inside a `/data=` blob.
    ///
    /// The two are **not adjacent** in a real URL (`!3m1!1e3!4m…!3d6.93!4d79.86`) and `!4d` can even
    /// precede `!3d`, so each is matched on its own rather than as one pattern.
    private static func placePin(in url: String) -> (Double, Double)? {
        point(
            lat: firstGroup(#"!3d(-?\d+\.?\d*)"#, in: url),
            lng: firstGroup(#"!4d(-?\d+\.?\d*)"#, in: url)
        )
    }

    /// `?q=lat,lng`, `?query=lat,lng`, `?ll=lat,lng`, `?center=lat,lng` — and `q=loc:lat,lng`.
    private static func parameterPair(in url: String, keys: [String]) -> (Double, Double)? {
        for key in keys {
            let pattern = #"[?&]"# + key + #"=(?:loc:)?(-?\d+\.?\d*)(?:,|%2C)(-?\d+\.?\d*)"#
            let groups = allGroups(pattern, in: url)
            if groups.count == 2, let pair = point(lat: groups[0], lng: groups[1]) {
                return pair
            }
        }
        return nil
    }

    /// `/@lat,lng,15z` — the camera, and the last thing tried for the reason in the type's note.
    private static func viewport(in url: String) -> (Double, Double)? {
        let groups = allGroups(#"@(-?\d+\.\d+),(-?\d+\.\d+)"#, in: url)
        guard groups.count == 2 else { return nil }
        return point(lat: groups[0], lng: groups[1])
    }

    /// A pair, if it is a coordinate at all.
    ///
    /// Range-checked and nothing more. A link to somewhere outside the operating cities is a
    /// *serviceability* answer — `400 unserviceable-area` on the fare estimate, in trilingual copy
    /// the platform owns — not a parse failure, and rejecting it here would tell a passenger their
    /// link was unreadable when it was read perfectly.
    private static func point(lat: Double?, lng: Double?) -> (Double, Double)? {
        guard let lat, let lng else { return nil }
        guard (-maxLat...maxLat).contains(lat), (-maxLng...maxLng).contains(lng) else { return nil }
        // `0,0` is in the Gulf of Guinea and is what a malformed URL degrades to far more often than
        // it is what somebody meant. Null Island is not a pickup point.
        guard !(lat == 0 && lng == 0) else { return nil }
        return (lat, lng)
    }

    // MARK: - Matching

    private static func firstGroup(_ pattern: String, in text: String) -> Double? {
        allGroups(pattern, in: text).first ?? nil
    }

    /// Every capture group of the first match, as doubles. Empty when the pattern did not match.
    ///
    /// `.caseInsensitive` throughout, because a pasted URL is whatever the sending app produced.
    private static func allGroups(_ pattern: String, in text: String) -> [Double?] {
        guard
            let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
            let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text))
        else {
            return []
        }
        return (1..<match.numberOfRanges).map { index in
            Range(match.range(at: index), in: text).flatMap { Double(text[$0]) }
        }
    }

    /// Every form this type reads lives on one of these hosts.
    ///
    /// Checked before the coordinate patterns rather than after, so a bare `6.93,79.86` — or a link
    /// to some other mapping site that happens to embed an `@lat,lng` — is *"couldn't read that
    /// link"* rather than a silently accepted pin. Country domains (`google.lk`, `google.co.uk`) are
    /// covered by the `google.` prefix.
    private static let googleHosts = ["google.", "goo.gl", "maps.app.goo.gl"]

    private static let shortHosts = ["maps.app.goo.gl", "goo.gl/maps"]

    private static let queryKeys = ["q", "query", "daddr", "destination"]
    private static let centreKeys = ["ll", "center", "sll"]

    private static let maxLat = 90.0
    private static let maxLng = 180.0
}

extension MapsLinkParse {

    /// The coordinate, for the one outcome that has one.
    var point: GeoPoint? {
        guard case .resolved(let lat, let lng) = self else { return nil }
        return GeoPoint(lat: lat, lng: lng)
    }
}
