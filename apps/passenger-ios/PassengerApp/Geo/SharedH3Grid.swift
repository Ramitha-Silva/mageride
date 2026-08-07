import Foundation
import MageRideH3
import MageRideShared

/// `:shared`'s ``H3Grid``, over the vendored H3 C library.
///
/// **This is the binding C017 and C085 both left to this component**, and it closes the last open
/// item on the iOS realtime plane: `platformH3Grid()` answers `nil` on this platform, so
/// `geoRealtimeModule`'s default *throws* on resolution — an app that opened a map would crash with
/// `H3GridUnavailableException`. `PassengerGraph` passes an instance of this to `startIosGraphWithH3`
/// and the app module's binding wins.
///
/// **Nothing here is grid arithmetic.** Every one of the four methods is one call into the reference
/// C library, which is the whole point: a client's cell ids must be *bit-identical* to the ones
/// `position-processor-svc` (C039) computes and `com.uber:h3` produces on Android, or the passenger
/// joins `cell:{h3index}` groups nothing publishes to — a failure that looks exactly like an empty
/// map with no error anywhere. See `shared/swiftpm/MageRideH3/VENDOR.md` for the provenance and why
/// vendoring beat a `cinterop` binding.
///
/// **An `NSObject` subclass, because a Kotlin interface reaches Swift as an Objective-C protocol.**
/// That is also why the parameters are `Int32` — Kotlin's `Int` is a C `int` on this bridge — and
/// why `Set<H3Cell>` comes back as an `NSSet` bridged to a Swift `Set` of the exported class.
///
/// **Thread-safe and side-effect free**, which ``H3Grid``'s own KDoc requires: the library holds no
/// global state and this type holds none either. SCR-PI-010 calls ``cellAt(point:resolution:)`` on
/// every position fix.
final class SharedH3Grid: NSObject, H3Grid {

    /// How many calls this build has had to answer with ``SharedH3Grid/unresolvable``.
    ///
    /// **Non-zero is a defect, not a condition.** It is here rather than a log line because the
    /// Kotlin interface has no failure channel (see ``cellAt(point:resolution:)``) and a silent
    /// substitution with no counter is exactly the kind of thing that is discovered six months later
    /// from a support ticket about an empty map. `H3GridTests` asserts it stays zero across the
    /// coordinates this platform actually uses.
    private(set) static var failures = 0

    /// What a refused call answers.
    ///
    /// **`H3Grid` cannot fail**: none of its four methods throws, and an Objective-C exception
    /// raised out of a Swift method into Kotlin would terminate the process rather than be caught
    /// (the C091 finding, from the other direction). So a refusal has to be a *value*, and index `0`
    /// is the right one: `H3Cell.isWellFormed` is `false` for it, `gridDisk` on it fails in turn and
    /// yields an empty set, and the net effect of a bad fix is therefore **no cells joined and no
    /// groups** rather than a wrong subscription. The next good fix repairs it, because
    /// `GeoCellSubscription` compares anchors and `0` is not equal to any real cell.
    ///
    /// In practice H3 refuses only two things at the resolutions this platform uses: an out-of-range
    /// resolution, which is a constant here, and a non-finite coordinate, which
    /// ``PassengerLocationSource`` filters before it publishes.
    private static let unresolvable = H3Cell(index: 0)

    func cellAt(point: GeoPoint, resolution: Int32) -> H3Cell {
        guard let index = try? H3.cell(at: point.lat, point.lng, resolution: resolution) else {
            return SharedH3Grid.refuse()
        }
        return H3Cell(index: Int64(bitPattern: index))
    }

    func gridDisk(origin: H3Cell, k: Int32) -> Set<H3Cell> {
        guard let disk = try? H3.gridDisk(UInt64(bitPattern: origin.index), k: k) else {
            SharedH3Grid.failures += 1
            return []
        }
        return Set(disk.map { H3Cell(index: Int64(bitPattern: $0)) })
    }

    func center(cell: H3Cell) -> GeoPoint {
        guard let centre = try? H3.centre(of: UInt64(bitPattern: cell.index)) else {
            SharedH3Grid.failures += 1
            // The null island rather than a throw, for ``unresolvable``'s reason. Nothing on this
            // surface *decides* from a cell centre — it is a camera hint — so a wrong one is a map
            // that opens in the wrong place and rights itself on the next fix.
            return GeoPoint(lat: 0, lng: 0)
        }
        return GeoPoint(lat: centre.latitude, lng: centre.longitude)
    }

    func parent(cell: H3Cell, resolution: Int32) -> H3Cell {
        guard let parent = try? H3.parent(of: UInt64(bitPattern: cell.index), resolution: resolution) else {
            return SharedH3Grid.refuse()
        }
        return H3Cell(index: Int64(bitPattern: parent))
    }

    private static func refuse() -> H3Cell {
        failures += 1
        return unresolvable
    }

    /// Resets the counter. Tests only — a production caller has nothing to do with it.
    static func resetFailures() {
        failures = 0
    }
}
