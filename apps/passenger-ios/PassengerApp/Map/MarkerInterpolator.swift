import Foundation
import MageRideShared

/// One vehicle as the map draws it.
///
/// The same type goes into ``MarkerInterpolator`` as a target and comes out of it tweened, which is
/// what keeps MAP-04 invisible to a caller: a screen hands over the frames it received and reads
/// back where the markers should be.
///
/// - `heading` defaults to 0° when the vehicle did not report one — an arrow pointing north is
///   wrong in a way a passenger can see and ignore, where no marker at all reads as no vehicle.
/// - `type` is the **wire** spelling (`three_wheeler`, not `threeWheeler`) because it goes straight
///   into a GeoJSON feature property that MAP-03's `match` expression is built over. See
///   ``VehicleToken/wire``.
struct MapVehicle: Equatable {
    let vehicleId: String
    var lat: Double
    var lng: Double
    var heading: Double = 0
    var type: String?
}

extension VehicleFrame {

    /// The frame as the map draws it (MAP-03, MAP-06).
    ///
    /// The conversion lives beside the interpolator rather than in the live plane because it is
    /// where the *live* plane's vocabulary ends: everything above this line thinks in
    /// `VehicleFrame`s from a contract, and everything below it thinks in markers.
    var asMapVehicle: MapVehicle {
        MapVehicle(
            vehicleId: vehicleId,
            lat: lat,
            lng: lng,
            heading: Double(heading?.int32Value ?? 0),
            type: type?.wire
        )
    }
}

/// MAP-04 — *"smooth marker animation between position updates (interpolation)"*.
///
/// **Why this is not a MapLibre feature.** The hub delivers a per-cell batch every **2–8 s**
/// (US-7.3, `signalr-hub.md` §3) rather than a fix per second: a vehicle's shape-source feature
/// therefore jumps hundreds of metres at a time, and a map of teleporting pins is what MAP-04 exists
/// to prevent. MapLibre does not tween a source's contents — a symbol layer draws whatever the
/// source holds on that frame — so the interpolation is ours, and it belongs in pure Swift where it
/// can be tested without a GL surface.
///
/// **The glide lasts as long as the last gap did, per vehicle.** A bus reporting every two seconds
/// glides for two and is therefore in continuous motion; a tuk reporting every eight glides for
/// eight. A fixed duration would make one of the two either sprint-and-freeze or crawl. The measured
/// gap is clamped to ``minimumDuration``…``maximumDuration``, so a burst of two batches inside one
/// second or a vehicle that went quiet for a minute cannot produce either extreme.
///
/// **The rendered marker is therefore one batch behind the truth, on purpose.** That is inherent to
/// smoothing; the alternative is drawing the newest fix instantly, which is the jump MAP-04 forbids.
/// Nothing *decides* anything from the interpolated value — the exact-distance rules
/// (`GeoCells.exactWithin`, the 100 m geofence) run against the frame, never against what the map
/// happens to be drawing this instant.
///
/// Not thread-safe: drive it from the display link that reads it, like the map it feeds.
final class MarkerInterpolator {

    private var order: [String] = []
    private var tracks: [String: Track] = [:]

    private let minimumDuration: TimeInterval
    private let maximumDuration: TimeInterval

    init(
        minimumDuration: TimeInterval = MarkerInterpolator.defaultMinimumDuration,
        maximumDuration: TimeInterval = MarkerInterpolator.defaultMaximumDuration
    ) {
        self.minimumDuration = minimumDuration
        self.maximumDuration = maximumDuration
    }

    /// Seats a new batch of targets, and drops every vehicle not in it.
    ///
    /// A vehicle seen for the first time appears **at** its position rather than gliding in from
    /// nowhere. Every later batch starts the glide from where the marker is *currently drawn*, not
    /// from its previous target, so a batch arriving mid-glide does not snap the marker backwards.
    ///
    /// Removal is immediate and never animated: a vehicle that went on hire (US-7.16), went stale
    /// (US-7.17) or whose Mode B share was revoked (D-22) must leave the map now, and a marker
    /// fading out over eight seconds is eight seconds of showing a vehicle the passenger is no
    /// longer entitled to see.
    ///
    /// - Parameters:
    ///   - vehicles: Everything that should be on the map, already filtered by the caller.
    ///   - now: A **monotonic** clock. `CACurrentMediaTime()` on a device; a counter in a test.
    ///     Never `Date()` — the wall clock steps when the network sets the time, and a marker would
    ///     jump or freeze with it.
    func onFrames(_ vehicles: [MapVehicle], now: TimeInterval) {
        for target in vehicles {
            if let existing = tracks[target.vehicleId] {
                tracks[target.vehicleId] = Track(
                    from: existing.position(at: now),
                    to: target,
                    startedAt: now,
                    duration: min(max(now - existing.lastFrameAt, minimumDuration), maximumDuration),
                    lastFrameAt: now
                )
            } else {
                order.append(target.vehicleId)
                tracks[target.vehicleId] = Track(
                    from: target, to: target, startedAt: now, duration: 0, lastFrameAt: now
                )
            }
        }

        let live = Set(vehicles.map(\.vehicleId))
        order.removeAll { !live.contains($0) }
        tracks = tracks.filter { live.contains($0.key) }
    }

    /// Forgets every track. The map was closed, or the passenger signed out.
    func clear() {
        order.removeAll()
        tracks.removeAll()
    }

    /// Whether every marker has reached its target — i.e. whether the frame loop may stop.
    func isSettled(at now: TimeInterval) -> Bool {
        tracks.values.allSatisfy { now >= $0.startedAt + $0.duration }
    }

    /// Where every marker should be drawn at `now`, in the order the vehicles were first seen.
    func markers(at now: TimeInterval) -> [MapVehicle] {
        order.compactMap { tracks[$0]?.position(at: now) }
    }

    private struct Track {
        let from: MapVehicle
        let to: MapVehicle
        let startedAt: TimeInterval
        let duration: TimeInterval
        let lastFrameAt: TimeInterval

        func position(at now: TimeInterval) -> MapVehicle {
            guard duration > 0 else { return to }
            let t = min(max((now - startedAt) / duration, 0), 1)
            var marker = to
            marker.lat = from.lat + (to.lat - from.lat) * t
            marker.lng = from.lng + (to.lng - from.lng) * t
            marker.heading = MarkerInterpolator.interpolateBearing(from: from.heading, to: to.heading, t: t)
            return marker
        }
    }

    /// The shortest glide.
    ///
    /// Two batches arriving inside one second — a reconnect snapshot immediately followed by the
    /// next live batch — would otherwise animate over a few milliseconds, which is the jump this
    /// class exists to remove.
    static let defaultMinimumDuration: TimeInterval = 1

    /// The longest.
    ///
    /// US-7.3's batch window tops out at 8 s and fanout-svc drops a vehicle unheard from inside its
    /// 60 s freshness window, so a gap longer than eight seconds means the vehicle was quiet rather
    /// than slow. Gliding for a minute would draw a smooth path nothing travelled.
    static let defaultMaximumDuration: TimeInterval = 8

    /// Interpolates a bearing the short way round.
    ///
    /// A vehicle turning from 350° to 10° has turned twenty degrees right, not three hundred and
    /// forty degrees left — and the naive linear form spins MAP-06's arrow through a whole rotation
    /// every time a vehicle crosses due north.
    static func interpolateBearing(from: Double, to: Double, t: Double) -> Double {
        let delta = (to - from + fullTurn + halfTurn).truncatingRemainder(dividingBy: fullTurn) - halfTurn
        return (from + delta * t + fullTurn).truncatingRemainder(dividingBy: fullTurn)
    }

    private static let fullTurn = 360.0
    private static let halfTurn = 180.0
}
