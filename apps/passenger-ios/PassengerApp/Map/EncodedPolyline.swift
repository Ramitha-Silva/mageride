import Foundation
import MageRideShared

/// Google's encoded-polyline algorithm, decode half.
///
/// `transit.yaml` types `TransitLeg.shape` and `TransitRoute.shape` as *"Encoded polyline of the
/// GTFS shape"*, which is the format `shapes.txt` is universally re-encoded into — a few thousand
/// coordinates as a couple of kilobytes of ASCII instead of a JSON array of pairs. SCR-PI-009 draws
/// that shape when a public route is selected, and SCR-PI-023's trip detail draws query-svc's, so
/// something has to turn it back into points.
///
/// **Nothing in `:shared` does this**, and it is not a MapLibre utility either — the iOS SDK's
/// polyline helpers live in the Turf package, which this target does not take. Forty lines of pure
/// Swift with no platform in them is the smaller answer, and it is testable in a unit test, which a
/// rendering path is not. The Android twin says the same about its own copy and adds *"if a third
/// caller appears it belongs in `:shared`"* — this is the second, and the note stands.
///
/// The algorithm: each coordinate is a **delta** from the previous one, scaled by 1e5, zig-zag
/// encoded so the sign lives in bit 0, then split into five-bit groups, each offset by 63 into
/// printable ASCII with bit 5 set on every group but the last.
enum EncodedPolyline {

    /// Decodes `encoded` into points, in order.
    ///
    /// **Malformed input yields a short list rather than an error.** A truncated shape is a server
    /// or feed problem, and the screen's answer to it is to draw the part it understood and carry
    /// on — a route line is decoration on a booking screen, not the booking.
    static func decode(_ encoded: String?) -> [GeoPoint] {
        guard let encoded, !encoded.isEmpty else { return [] }

        let bytes = Array(encoded.utf8)
        var points: [GeoPoint] = []
        var index = 0
        var lat = 0
        var lng = 0

        while index < bytes.count {
            // Both halves or neither: a latitude applied without its longitude would move every
            // subsequent point, so a pair that cannot be completed ends the decode.
            guard let latDelta = readValue(bytes, from: index),
                  let lngDelta = readValue(bytes, from: latDelta.next)
            else { break }

            index = lngDelta.next
            lat += latDelta.value
            lng += lngDelta.value
            points.append(GeoPoint(lat: Double(lat) / scale, lng: Double(lng) / scale))
        }

        return points
    }

    /// One zig-zag varint starting at `from`, or `nil` if the input ran out mid-value.
    ///
    /// `nil` rather than a partial read: half a delta applied to the running latitude would put a
    /// point somewhere arbitrary, and one wrong vertex in a route line is more misleading than a
    /// line that simply stops.
    ///
    /// Over `[UInt8]` rather than `String.Index`: a `String` is a sequence of grapheme clusters and
    /// this format is bytes. Indexing a Swift string by integer offset is also O(n), which on a
    /// several-thousand-point GTFS shape is the difference between a decode and a stall.
    private static func readValue(_ bytes: [UInt8], from: Int) -> (value: Int, next: Int)? {
        var index = from
        var shift = 0
        var result = 0

        while index < bytes.count {
            let byte = Int(bytes[index]) - offset
            index += 1
            if byte < 0 { return nil }
            result |= (byte & groupMask) << shift
            shift += groupBits
            if byte < continuationMask {
                // Zig-zag: bit 0 is the sign, so a negative delta is `~(v >> 1)` and a positive one
                // is `v >> 1`. Encoding the sign in the low bit is what keeps small negative deltas
                // — most of a route heading south or west — one character wide.
                let value = (result & 1) != 0 ? ~(result >> 1) : result >> 1
                return (value, index)
            }
        }

        return nil
    }

    /// 1e5 — five decimal places, about a metre, which is what the format fixes.
    private static let scale = 100_000.0

    /// Printable-ASCII offset: every group is stored as `group + 63`.
    private static let offset = 63

    /// Bit 5 set means "another group follows".
    private static let continuationMask = 0x20

    private static let groupMask = 0x1f
    private static let groupBits = 5
}
