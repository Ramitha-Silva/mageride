import CH3
import Foundation

/// The H3 operations MageRide needs, and nothing else.
///
/// This is the whole of what this package adds to the vendored C: unit conversion, error checking
/// and one allocation. There is deliberately **no** grid arithmetic here — see `VENDOR.md` and
/// `:shared`'s `H3Grid` for why a re-derived grid is the one failure mode that is invisible.
///
/// The four members mirror `lk.mageride.shared.domain.geo.H3Grid` exactly, so the adapter in the app
/// that conforms to the exported Kotlin protocol is a straight transliteration and has nowhere to
/// introduce a rule of its own.
///
/// **Degrees in, degrees out.** H3's C API is radians and every MageRide coordinate is degrees
/// (`GeoPoint.lat` / `.lng` are what the contracts carry), so the conversion happens here, once,
/// rather than at four call sites. `degsToRads` and `radsToDegs` are H3's own — using Foundation's
/// `.pi` instead would be a second constant.
///
/// **Thread-safe and side-effect free**, which `H3Grid`'s KDoc requires: the C library holds no
/// global state and every function here is a pure computation over its arguments. The passenger map
/// calls ``cell(at:resolution:)`` on every position fix.
public enum H3 {

    /// The 64-bit index of the cell containing a coordinate.
    ///
    /// - Parameters:
    ///   - latitude: Degrees.
    ///   - longitude: Degrees.
    ///   - resolution: `0...15`. MageRide uses 7 (the passenger view) and 5 (the dispatch index).
    /// - Throws: ``H3Error`` when H3 refuses the arguments — an out-of-range resolution, or a
    ///   coordinate that is not finite. A silent `0` would be a well-formed-looking group name that
    ///   nothing publishes to, which is exactly the failure this package exists to prevent.
    public static func cell(at latitude: Double, _ longitude: Double, resolution: Int32) throws -> UInt64 {
        var point = LatLng(lat: degsToRads(latitude), lng: degsToRads(longitude))
        var index: H3Index = 0
        try check(latLngToCell(&point, resolution, &index))
        return index
    }

    /// `origin` plus every cell within `k` grid steps — H3's `gridDisk`.
    ///
    /// A hexagon's disk holds `1 + 3k(k + 1)` cells: **19** at `k = 2`, which is R-06's 3 km
    /// passenger view. A disk centred on one of the twelve pentagons holds five fewer per ring —
    /// none of them is anywhere near Sri Lanka, but nothing here assumes the count, which is why the
    /// zero slots H3 leaves in the output buffer are filtered rather than trusted to be absent.
    public static func gridDisk(_ origin: UInt64, k: Int32) throws -> [UInt64] {
        var capacity: Int64 = 0
        try check(maxGridDiskSize(k, &capacity))

        var out = [H3Index](repeating: 0, count: Int(capacity))
        try out.withUnsafeMutableBufferPointer { buffer in
            try check(CH3.gridDisk(origin, k, buffer.baseAddress))
        }
        return out.filter { $0 != 0 }
    }

    /// The cell's centre, in degrees.
    public static func centre(of cell: UInt64) throws -> (latitude: Double, longitude: Double) {
        var point = LatLng()
        try check(cellToLatLng(cell, &point))
        return (radsToDegs(point.lat), radsToDegs(point.lng))
    }

    /// The ancestor of `cell` at a coarser resolution — how a res-7 view cell maps onto the res-5
    /// dispatch index.
    public static func parent(of cell: UInt64, resolution: Int32) throws -> UInt64 {
        var index: H3Index = 0
        try check(cellToParent(cell, resolution, &index))
        return index
    }

    /// Whether H3 itself considers this a resolvable cell index.
    ///
    /// Stronger than `H3Cell.isWellFormed` in `:shared`, which is a *structural* check on the bit
    /// layout and says so: this one additionally rejects the deleted subsequence of a pentagon,
    /// which needs the base-cell tables the library carries.
    public static func isValid(_ cell: UInt64) -> Bool {
        isValidCell(cell) != 0
    }

    private static func check(_ error: H3Error) throws {
        guard error != 0 else { return }
        throw H3Error_(code: error)
    }
}

/// An H3 call that failed, carrying the library's own error code.
///
/// Named with a trailing underscore because `H3Error` is the C library's own typedef for the code
/// itself, and shadowing it inside this module would make the `throws` signatures above read as
/// though they threw the integer.
public struct H3Error_: Error, Equatable {

    /// H3's `H3Error` code — `1` is a bad resolution, `2` a bad argument, `3` a bad cell, and so on
    /// through the `E_*` constants in `h3api.h`.
    public let code: UInt32

    /// H3's own description of the code.
    public var message: String {
        String(cString: describeH3Error(code))
    }
}
